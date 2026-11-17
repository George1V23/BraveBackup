using System.Text;
using System.Text.Json;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BravePasswordCsvExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompleteCsv_" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Profile => Path.Combine(Data, "Default");
    private string Destination => Path.Combine(_root, "output", "passwords.csv");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private BraveAppInfo App(string version = "1.102.13") => new(BraveAppChannel.Stable, version, "synthetic", Data);
    private BraveProfileInfo ProfileInfo => new("Synthetic", Profile, Path.Combine(Profile, "Bookmarks"));
    private PasswordKeyMaterial? _key;
    private void Prepare(string os = "linux", bool empty = false)
    {
        Directory.CreateDirectory(Profile); Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        var database = Path.Combine(Profile, "Login Data");
        File.Copy(Path.Combine(Fixtures, "Passwords", "LoginData.v43." + (empty ? "empty" : os) + ".sqlite"), database);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery();
        }
        File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName));
        if (os == "windows") File.WriteAllText(Path.Combine(Data, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
    }
    private Task<BravePasswordCsvExportResult> Export(Func<PasswordCsvExportWarning, CancellationToken, Task<bool>> confirm,
        string os = "linux", string version = "1.102.13", bool selected = true, CancellationToken token = default) =>
        BravePasswordCsvExport.CreateAsync(App(version), ProfileInfo,
            os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
            Destination, selected, os == "windows" ? PasswordCsvLineEnding.WindowsCrLf : PasswordCsvLineEnding.LinuxLf, confirm,
            (_, _) =>
            {
                using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
                _key = new PasswordKeyMaterial(os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
                    Convert.FromHexString(vectors.RootElement.GetProperty(os + "KeyHex").GetString()!)); return _key;
            }, () => ProcessPresence.Stopped, token);

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public async Task CompleteOperationCapturesProjectsConfirmsAndSavesWithoutSourceChanges(string os)
    {
        Prepare(os); var sources = Directory.GetFiles(Data, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes); var calls = 0;
        var result = await Export((warning, _) =>
        {
            calls++; Assert.Equal(Destination, warning.Destination); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
            Assert.Contains("readable by anyone with file access", warning.Message);
            Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], _key!));
            return Task.FromResult(true);
        }, os);
        Assert.Equal(1, calls); Assert.Equal(1, result.SourceLogins); Assert.Equal(1, result.CredentialGroups); Assert.Equal(1, result.CsvRows); Assert.Equal(2, result.ExcludedEntries);
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
        var ending = os == "windows" ? "\r\n" : "\n";
        var password = vectors.RootElement.GetProperty("passwordUtf8").GetString()!.Replace("\"", "\"\"");
        var note = vectors.RootElement.GetProperty("noteUtf8").GetString()!.Replace("\"", "\"\"");
        Assert.Equal("name,url,username,password,note" + ending + "example.invalid,https://example.invalid/login,fixture-user,\"" + password + "\",\"" + note + "\"" + ending,
            Encoding.UTF8.GetString(File.ReadAllBytes(Destination)));
        foreach (var source in sources) Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Theory]
    [InlineData("1.103.1")]
    [InlineData("unknown")]
    [InlineData("1.102.13.1")]
    public async Task UnresearchedVersionStopsBeforeCredentialAccess(string version)
    {
        Prepare(); var prompts = 0;
        Assert.Equal("passwords-csv-version-unsupported", (await Assert.ThrowsAsync<BackupOperationException>(() => Export((_, _) =>
        { prompts++; return Task.FromResult(true); }, version: version))).Code);
        Assert.Null(_key); Assert.Equal(0, prompts); Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task NoExplicitChoiceOrPreCancellationNeverAccessesCredentials()
    {
        Prepare();
        await Assert.ThrowsAsync<BackupOperationException>(() => Export((_, _) => throw new Exception(), selected: false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Export((_, _) => throw new Exception(), token: new CancellationToken(true)));
        Assert.Null(_key); Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclineOrSourceChangePreservesExistingDestination(bool sourceChange)
    {
        Prepare(); File.WriteAllText(Destination, "synthetic-original");
        var exception = await Record.ExceptionAsync(() => Export((_, _) =>
        {
            if (sourceChange) File.AppendAllText(Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName), "changed");
            return Task.FromResult(sourceChange);
        }));
        Assert.NotNull(exception); Assert.Equal("synthetic-original", File.ReadAllText(Destination)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task EmptyValidatedStoreNeedsNeitherUnlockNorAffiliationCache()
    {
        Prepare(empty: true); File.Delete(Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName));
        var result = await Export((_, _) => Task.FromResult(true), version: "1.102.13.0");
        Assert.Null(_key); Assert.Equal(0, result.CsvRows); Assert.Equal("name,url,username,password,note\n", File.ReadAllText(Destination));
    }

    [Fact]
    public async Task MissingCacheOrUnsupportedNamedNoteDoesNotProducePartialCsv()
    {
        Prepare(); File.Delete(Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName));
        await Assert.ThrowsAsync<BackupOperationException>(() => Export((_, _) => throw new Exception("Must not warn")));
        Assert.False(File.Exists(Destination));
        File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Profile, "Login Data"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE password_notes SET key='named'"; command.ExecuteNonQuery();
        }
        Assert.Equal("passwords-csv-notes-unsupported", (await Assert.ThrowsAsync<BackupOperationException>(() => Export((_, _) => throw new Exception("Must not warn")))).Code);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task CancellationInFreshWarningPreservesExistingDestination()
    {
        Prepare(); File.WriteAllText(Destination, "synthetic-original"); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Export((_, _) =>
        { cancellation.Cancel(); return Task.FromResult(true); }, token: cancellation.Token));
        Assert.Equal("synthetic-original", File.ReadAllText(Destination)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task ProtectedDestinationIsRejectedBeforeUnlockOrWarning()
    {
        Prepare(); var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BravePasswordCsvExport.CreateAsync(App(), ProfileInfo,
            PasswordEncryptionProvider.LinuxKeyringV11, Path.Combine(Profile, "passwords.csv"), true, PasswordCsvLineEnding.LinuxLf,
            (_, _) => { calls++; return Task.FromResult(true); }, (_, _) => throw new Exception("Must not unlock")));
        Assert.Equal(0, calls); Assert.Null(_key);
    }

    public void Dispose() { _key?.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
