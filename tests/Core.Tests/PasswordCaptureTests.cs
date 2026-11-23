using System.Text;
using System.Text.Json;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PasswordCapture_" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "Default");
    private string State => Path.Combine(_root, "Local State");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords");
    private static JsonElement Vectors
    {
        get { using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Crypto.synthetic.json"))); return document.RootElement.Clone(); }
    }
    private void Prepare(string os = "windows")
    {
        Directory.CreateDirectory(Profile);
        File.Copy(Path.Combine(Fixtures, "LoginData.v43." + os + ".sqlite"), Path.Combine(Profile, "Login Data"));
        File.WriteAllText(State, "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
    }
    private static PasswordKeyMaterial Key(string os) => new(os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
        Convert.FromHexString(Vectors.GetProperty(os + "KeyHex").GetString()!));
    private BravePasswordCapture Read(string os = "windows", Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? unlock = null,
        Func<ProcessPresence>? observe = null, CancellationToken token = default) => BravePasswordCapture.Read(Profile,
        os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
        unlock ?? ((_, _) => Key(os)), observe ?? (() => ProcessPresence.Stopped), token);
    private void Edit(string sql, byte[]? bytes = null, string name = "Login Data")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Profile, name), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (bytes is not null) command.Parameters.AddWithValue("$bytes", bytes);
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void CapturesBothStoresWithOneOwnedKeyAndNoSourceWrites(string os)
    {
        Prepare(os);
        File.Copy(Path.Combine(Profile, "Login Data"), Path.Combine(Profile, "Login Data For Account"));
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        PasswordKeyMaterial? borrowedKey = null;
        ReadOnlyMemory<byte> borrowedState = default;
        var calls = 0;
        using var capture = Read(os, (state, token) =>
        {
            calls++;
            borrowedState = state;
            borrowedKey = os == "windows"
                ? BraveWindowsPasswordKey.Unlock(state, _ => Convert.FromHexString(Vectors.GetProperty("windowsKeyHex").GetString()!), token)
                : BraveLinuxPasswordKey.Unlock((identity, _) =>
                { Assert.Equal("brave", identity); return Encoding.UTF8.GetBytes(Vectors.GetProperty("syntheticLinuxSecret").GetString()!); }, token);
            return borrowedKey;
        });
        Assert.Equal(1, calls);
        Assert.Equal(2, capture.Databases.Count);
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], borrowedKey!));
        foreach (var database in capture.Databases.Values)
        {
            var row = Assert.Single(database.Rows);
            Assert.Equal(Vectors.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(row.Password.Utf8.Span));
            Assert.Equal(Vectors.GetProperty("noteUtf8").GetString(), Encoding.UTF8.GetString(Assert.Single(row.Notes).Value.Utf8.Span));
            Assert.Equal(1, database.BlockedEntries);
        }
        capture.VerifyUnchanged();
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
        Assert.Equal(before.Count, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        var password = capture.Databases["Login Data"].Rows[0].Password.Utf8;
        capture.Dispose();
        Assert.All(password.ToArray(), b => Assert.Equal(0, b));
        Assert.All(borrowedState.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => capture.Databases);
        Assert.Throws<ObjectDisposedException>(() => capture.VerifyUnchanged());
    }

    [Fact]
    public void CapturesNativeTextBackedNoteAsCiphertextWithoutChangingTheSource()
    {
        Prepare("linux");
        Edit("UPDATE password_notes SET value = CAST(value AS TEXT)");
        var database = Path.Combine(Profile, "Login Data");
        var original = File.ReadAllBytes(database);

        using var capture = Read("linux");

        var row = Assert.Single(capture.Databases["Login Data"].Rows);
        var note = Assert.Single(row.Notes);
        Assert.Equal(Vectors.GetProperty("noteUtf8").GetString(), Encoding.UTF8.GetString(note.Value.Utf8.Span));
        capture.VerifyUnchanged();
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void ValidatedEmptyStoreDoesNotRequestKeyOrLocalState(string os)
    {
        Prepare("empty");
        File.Delete(State);
        using var capture = Read(os, (_, _) => throw new Exception("Must not unlock"));
        Assert.Empty(capture.Databases["Login Data"].Rows);
    }

    [Fact]
    public void ActualEmptyValuesNeedNoFabricatedKey()
    {
        Prepare();
        Edit("UPDATE logins SET password_value = X''; UPDATE password_notes SET value = X'';");
        File.Delete(State);
        using var capture = Read(unlock: (_, _) => throw new Exception("Must not unlock"));
        var row = Assert.Single(capture.Databases["Login Data"].Rows);
        Assert.Empty(row.Password.Utf8.ToArray());
        Assert.Empty(Assert.Single(row.Notes).Value.Utf8.ToArray());
    }

    [Theory]
    [InlineData("v20")]
    [InlineData("v12")]
    [InlineData("v11")]
    public void UnsupportedFrameRejectsBeforeKeyAccess(string prefix)
    {
        Prepare();
        Edit("UPDATE logins SET password_value=$bytes WHERE id=1", Encoding.ASCII.GetBytes(prefix + "unsupported"));
        File.Delete(State);
        Assert.Equal("passwords-encryption-unsupported", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"))).Code);
    }

    [Fact]
    public void IncompleteFrameRejectsBeforeKeyAccess()
    {
        Prepare();
        Edit("UPDATE password_notes SET value=$bytes", "v10"u8.ToArray());
        Assert.Equal("passwords-decode-failed", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"))).Code);
    }

    [Fact]
    public void MissingKeySourceAndRunningBrowserNeverUnlock()
    {
        Prepare();
        File.Delete(State);
        Assert.Equal("passwords-key-source-invalid", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"))).Code);
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"), observe: () => ProcessPresence.Running)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangesDuringUnlockRejectAndDisposeReturnedKey(bool keySource)
    {
        Prepare();
        var key = Key("windows");
        Assert.Equal(keySource ? "passwords-key-source-changed" : "passwords-source-changed",
            Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) =>
            {
                var path = keySource ? State : Path.Combine(Profile, "Login Data");
                var timestamp = File.GetLastWriteTimeUtc(path);
                var bytes = File.ReadAllBytes(path);
                bytes[^1] ^= 1;
                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, timestamp);
                return key;
            })).Code);
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
    }

    [Fact]
    public void BrowserStartingDuringUnlockRejects()
    {
        Prepare();
        var running = false;
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) =>
        { running = true; return Key("windows"); }, observe: () => running ? ProcessPresence.Running : ProcessPresence.Stopped)).Code);
    }

    [Fact]
    public void LaterStoreFailureReturnsNoPartialCaptureAndClearsBorrowedState()
    {
        Prepare();
        File.Copy(Path.Combine(Profile, "Login Data"), Path.Combine(Profile, "Login Data For Account"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Crypto.synthetic.json")));
        var bad = Convert.FromHexString(document.RootElement.GetProperty("valuesHex").GetProperty("windows-v10-note").GetString()!);
        bad[^1] ^= 1;
        Edit("UPDATE password_notes SET value=$bytes", bad, "Login Data For Account");
        ReadOnlyMemory<byte> borrowed = default;
        Assert.Equal("passwords-decode-failed", Assert.Throws<BackupOperationException>(() => Read(unlock: (state, _) =>
        { borrowed = state; return Key("windows"); })).Code);
        Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void CancellationDuringUnlockDisposesKeyAndState()
    {
        Prepare();
        using var cancellation = new CancellationTokenSource();
        var key = Key("windows");
        ReadOnlyMemory<byte> borrowed = default;
        Assert.Throws<OperationCanceledException>(() => Read(unlock: (state, _) =>
        { borrowed = state; cancellation.Cancel(); return key; }, token: cancellation.Token));
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
        Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void WrongProviderKeyFailsAndIsDisposed()
    {
        Prepare();
        var key = Key("linux");
        Assert.Equal("passwords-encryption-unsupported", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => key)).Code);
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
    }

    [Fact]
    public void PublicationVerificationDetectsKeySourceChange()
    {
        Prepare();
        using var capture = Read();
        File.AppendAllText(State, " ");
        Assert.Equal("passwords-key-source-changed", Assert.Throws<BackupOperationException>(() => capture.VerifyUnchanged()).Code);
    }

    [Fact]
    public void SymlinkKeySourceIsRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        Prepare();
        var actual = Path.Combine(_root, "actual-state");
        File.Move(State, actual);
        File.CreateSymbolicLink(State, actual);
        Assert.Equal("passwords-key-source-invalid", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"))).Code);
    }

    [Fact]
    public void NullKeyResultFailsWithoutPartialCapture()
    {
        Prepare();
        Assert.Equal("passwords-key-unlock-failed", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => null!)).Code);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("cancelled")]
    [InlineData("unknown-browser")]
    public void CaptureFailuresNeverReachUnlock(string failure)
    {
        Prepare();
        if (failure == "corrupt") File.WriteAllBytes(Path.Combine(Profile, "Login Data"), "not a database"u8.ToArray());
        var token = new CancellationToken(failure == "cancelled");
        var exception = Record.Exception(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"),
            observe: () => failure == "unknown-browser" ? ProcessPresence.Unknown : ProcessPresence.Stopped, token: token));
        if (failure == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else Assert.IsType<BackupOperationException>(exception);
    }

    [Fact]
    public void AddedCompanionDuringUnlockIsRejected()
    {
        Prepare();
        Assert.Equal("passwords-source-changed", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) =>
        { File.WriteAllBytes(Path.Combine(Profile, "Login Data-wal"), []); return Key("windows"); })).Code);
    }

    [Fact]
    public void OversizedKeySourceDoesNotUnlock()
    {
        Prepare();
        using (var stream = File.OpenWrite(State)) stream.SetLength(4 * 1024 * 1024 + 1);
        Assert.Equal("passwords-key-source-invalid", Assert.Throws<BackupOperationException>(() => Read(unlock: (_, _) => throw new Exception("Must not unlock"))).Code);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void PortableMappingOwnsIndependentSecrets(string os)
    {
        Prepare(os);
        File.Copy(Path.Combine(Profile, "Login Data"), Path.Combine(Profile, "Login Data For Account"));
        using var capture = Read(os);
        using var portable = BravePortablePasswords.Create(capture);
        Assert.Equal(new[] { PortablePasswordStoreKind.Profile, PortablePasswordStoreKind.Account }, portable.Stores.Select(s => s.Kind));
        var source = capture.Databases["Login Data"].Rows[0].Password.Utf8;
        var copied = portable.Stores[0].Logins[0].Password.Utf8;
        capture.Dispose();
        Assert.All(source.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(Vectors.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(copied.Span));
        using var encoded = PasswordPayloadFormat.Encode(portable);
        using var restored = PasswordPayloadFormat.Decode(encoded.Bytes.Span);
        Assert.Equal(copied.ToArray(), restored.Stores[0].Logins[0].Password.Utf8.ToArray());
        portable.Dispose();
        Assert.All(copied.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void PortableMappingRejectsStaleCaptureAndCancellation()
    {
        Prepare();
        using var capture = Read();
        Assert.Throws<OperationCanceledException>(() => BravePortablePasswords.Create(capture, new CancellationToken(true)));
        File.AppendAllText(State, " ");
        Assert.Equal("passwords-key-source-changed", Assert.Throws<BackupOperationException>(() => BravePortablePasswords.Create(capture)).Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
