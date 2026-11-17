using System.Text;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvFileExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CsvSave_" + Guid.NewGuid().ToString("N"));
    private readonly SensitiveUtf8 _password = new("synthetic-secret"u8);
    private readonly SensitiveUtf8 _note = new("synthetic-note"u8);
    private string Target => Path.Combine(_root, "passwords.csv");
    private PasswordCsvRow[] Rows => [new("synthetic", "https://synthetic.invalid/", "user", _password, _note)];
    public PasswordCsvFileExportTests() => Directory.CreateDirectory(_root);
    private Task<PasswordCsvExportResult> Save(Func<PasswordCsvExportWarning, CancellationToken, Task<bool>> confirm,
        Action? verify = null, bool explicitChoice = true, string? target = null, CancellationToken token = default,
        IReadOnlyList<string>? roots = null) => PasswordCsvFileExport.SaveAsync(Rows, PasswordCsvLineEnding.LinuxLf,
        target ?? Target, explicitChoice, roots ?? [], verify ?? (() => { }), confirm, token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshWarningPrecedesAnyWriteAndSuccessfulSaveIsExact(bool existing)
    {
        if (existing) File.WriteAllText(Target, "synthetic-old");
        var checks = 0; var warnings = 0;
        var result = await Save((warning, token) =>
        {
            warnings++;
            Assert.Equal(Target, warning.Destination); Assert.Equal(existing, warning.ReplacesExistingFile);
            Assert.Contains("readable by anyone with file access", warning.Message);
            Assert.Contains(Target, warning.Message); Assert.Contains("temporary plaintext", warning.Message);
            Assert.DoesNotContain("synthetic-secret", warning.Message);
            Assert.Equal(existing ? 1 : 0, Directory.GetFiles(_root).Length);
            return Task.FromResult(true);
        }, () => checks++);
        Assert.Equal(1, warnings); Assert.Equal(3, checks); Assert.Equal(1, result.RowCount);
        using var expected = PasswordCsvFormat.Encode(Rows, PasswordCsvLineEnding.LinuxLf);
        Assert.Equal(expected.Bytes.ToArray(), File.ReadAllBytes(Target));
        Assert.Equal(expected.Bytes.Length, result.Bytes); Assert.Single(Directory.GetFiles(_root));
        Assert.Equal("synthetic-secret", Encoding.UTF8.GetString(_password.Utf8.Span));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Target));
    }

    [Fact]
    public async Task DeclinePreservesExistingDestinationAndCreatesNothing()
    {
        File.WriteAllText(Target, "synthetic-original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save((_, _) => Task.FromResult(false)));
        Assert.Equal("synthetic-original", File.ReadAllText(Target)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task CsvMustBeExplicitAndConsentIsNeverCached()
    {
        var warnings = 0;
        Task<bool> Confirm(PasswordCsvExportWarning _, CancellationToken token) { warnings++; return Task.FromResult(true); }
        Assert.Equal("passwords-csv-not-selected", (await Assert.ThrowsAsync<BackupOperationException>(() => Save(Confirm, explicitChoice: false))).Code);
        Assert.Equal(0, warnings); Assert.Empty(Directory.GetFiles(_root));
        await Save(Confirm); await Save(Confirm);
        Assert.Equal(2, warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationChangedDuringWarningNeedsFreshOperation(bool existing)
    {
        if (existing) File.WriteAllText(Target, "synthetic-old");
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Save((_, _) =>
        { var stamp = existing ? File.GetLastWriteTimeUtc(Target) : (DateTime?)null; File.WriteAllText(Target, "synthetic-new");
            if (stamp is not null) File.SetLastWriteTimeUtc(Target, stamp.Value); return Task.FromResult(true); }));
        Assert.Equal("destination-changed", error.Code);
        Assert.Equal("synthetic-new", File.ReadAllText(Target)); Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SourceFailureBeforeOrAfterStagingPreservesDestinationAndCleansTemporary(int failAt)
    {
        File.WriteAllText(Target, "synthetic-original");
        var checks = 0;
        await Assert.ThrowsAsync<BackupOperationException>(() => Save((_, _) => Task.FromResult(true), () =>
        { if (++checks == failAt) throw new BackupOperationException("source-changed", "Synthetic source changed."); }));
        Assert.Equal("synthetic-original", File.ReadAllText(Target)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task CancellationAfterStagingCleansPlaintextTemporary()
    {
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save((_, _) => Task.FromResult(true), () =>
        { if (++checks == 3) cancellation.Cancel(); }, token: cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DestinationChangeAfterStagingIsDetected(int changeAt)
    {
        var checks = 0;
        // Changes during either source recheck must be caught before publication.
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Save((_, _) => Task.FromResult(true), () =>
        { if (++checks == changeAt) File.WriteAllText(Target, "synthetic-concurrent"); }));
        Assert.Equal("destination-changed", error.Code);
        Assert.Equal("synthetic-concurrent", File.ReadAllText(Target)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task InvalidDestinationsNeverShowConsentOrWrite()
    {
        var calls = 0;
        Task<bool> Confirm(PasswordCsvExportWarning _, CancellationToken token) { calls++; return Task.FromResult(true); }
        await Assert.ThrowsAsync<BackupOperationException>(() => Save(Confirm, target: Path.Combine(_root, "bad.txt")));
        await Assert.ThrowsAsync<BackupOperationException>(() => Save(Confirm, target: Path.Combine(_root, "absent", "out.csv")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Confirm, roots: [_root]));
        Assert.Equal(0, calls); Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task WarningExceptionOrCancellationWritesNothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save((_, _) => throw new InvalidOperationException("Synthetic UI failure")));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save((_, _) =>
        { cancellation.Cancel(); return Task.FromResult(true); }, token: cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task FirstSourceFailureOrPreCancellationDoesNotRequestConsent()
    {
        var warnings = 0;
        Task<bool> Confirm(PasswordCsvExportWarning _, CancellationToken token) { warnings++; return Task.FromResult(true); }
        await Assert.ThrowsAsync<BackupOperationException>(() => Save(Confirm, () => throw new BackupOperationException("source-changed", "Synthetic source changed.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save(Confirm, token: new CancellationToken(true)));
        Assert.Equal(0, warnings); Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task InvalidRowAfterConsentPreservesExistingFile()
    {
        File.WriteAllText(Target, "synthetic-original");
        _password.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Save((_, _) => Task.FromResult(true)));
        Assert.Equal("synthetic-original", File.ReadAllText(Target)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task OversizedExistingDestinationIsRefusedBeforeConsent()
    {
        using (var stream = File.Create(Target)) stream.SetLength(PasswordCsvFormat.MaximumBytes + 1L);
        var warnings = 0;
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Save((_, _) =>
        { warnings++; return Task.FromResult(true); }));
        Assert.Equal("passwords-csv-destination-limit", error.Code);
        Assert.Equal(0, warnings); Assert.Single(Directory.GetFiles(_root));
        Assert.Equal(PasswordCsvFormat.MaximumBytes + 1L, new FileInfo(Target).Length);
    }

    public void Dispose()
    {
        _password.Dispose(); _note.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
