using System.IO.Compression;
using System.Text;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestorePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestorePreview_" + Guid.NewGuid().ToString("N"));
    private const string Secret = "synthetic-unlock-key";
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private string CreateArchive(int version = 8, bool encrypted = false, bool raw = false)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bravebackup");
        if (version < 6)
        {
            string[] fixtures = ["Archive.v1.bravebackup", "Archive.v2.settings.bravebackup", "Archive.v3.extensions.bravebackup",
                "Sessions/Archive.v4.sessions.bravebackup", "TabManagerPlus/Archive.v5.tabmanagerplus.bravebackup"];
            File.Copy(Path.Combine(Fixtures, fixtures[version - 1]), path); return path;
        }
        var installation = Guid.NewGuid(); var profile = Guid.NewGuid();
        var prefix = $"installations/{installation:D}/profiles/{profile:D}/passwords/";
        using var plain = new MemoryStream();
        using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
        {
            ArchiveFile[] files = raw ?
            [BackupArchive.WriteFile(zip, prefix + "raw-index-v1.json", "raw-password-index",
                "{\"schemaVersion\":1,\"captureKind\":\"raw-native-password-files\",\"portableCaptureCompleted\":false,\"reasonCode\":\"passwords-database-invalid\",\"missingMainDatabases\":[]}"u8.ToArray(), "balanced"),
             BackupArchive.WriteFile(zip, prefix + "raw/Login Data", "native-password-file", "synthetic"u8.ToArray(), "balanced")] :
            [BackupArchive.WriteFile(zip, prefix + (version == 8 ? "passwords.csv" : "portable-v1.bin"),
                version == 8 ? "plaintext-password-csv" : "portable-passwords",
                version == 8 ? "name,url,username,password,note\nSynthetic,https://example.test/,fixture-user,synthetic-secret,\n"u8.ToArray() :
                    File.ReadAllBytes(Path.Combine(Fixtures, "PortablePasswords", "Payload.v1.synthetic.bin")), "balanced")];
            var category = new ArchiveCategory("passwords", version - 5, files);
            BackupArchive.WriteManifest(zip, new("BraveBackupArchive", version, Guid.NewGuid(), DateTimeOffset.UtcNow, "synthetic-app",
                ["passwords-v" + category.Version], "balanced", [new(installation, "Brave", "Stable", "1.102.13", [new(profile, "Synthetic profile", "Default", [category])])]));
        }
        using var output = File.Create(path);
        if (encrypted || version < 8) { using var writer = ArchiveEncryption.CreateWriter(output, Secret); writer.Write(plain.ToArray()); }
        else output.Write(plain.ToArray());
        return path;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ReaderValidatesExistingVersionsWithoutSourceChanges(int version)
    {
        var path = CreateArchive(version); var before = File.ReadAllBytes(path);
        var result = BraveArchiveInspection.Read(path, version is 6 or 7 ? Secret : null);
        Assert.Equal(version, result.Manifest!.FormatVersion); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task EncryptedSourceRequiresExplicitUnlockAndNeverPreviewsCredentials()
    {
        var path = CreateArchive(encrypted: true);
        using var vm = new RestorePreviewViewModel(); vm.SourcePath = path; await Idle(vm);
        Assert.True(vm.NeedsUnlock); Assert.Empty(vm.Installations); Assert.Empty(vm.Summary);
        vm.Password = "wrong-synthetic"; await vm.UnlockAsync();
        Assert.True(vm.NeedsUnlock); Assert.Empty(vm.Installations); Assert.Empty(vm.Password);
        vm.Password = Secret; await vm.UnlockAsync();
        Assert.False(vm.NeedsUnlock); Assert.Single(vm.Installations);
        Assert.Contains("synthetic-app", vm.Summary); Assert.Contains("format: 8", vm.Summary);
        var category = Assert.Single(Assert.Single(vm.Installations[0].Profiles).Categories);
        Assert.Contains("CSV", category.Description); Assert.DoesNotContain("fixture-user", category.ToString());
        Assert.DoesNotContain("synthetic-secret", vm.Summary + vm.Status + category.ToString()); Assert.Empty(vm.Password);
        vm.Leave(); Assert.Empty(vm.Installations); Assert.Empty(vm.Summary); Assert.Empty(vm.Password);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PasswordRepresentationIsExplicit(bool raw)
    {
        var path = CreateArchive(raw ? 7 : 6, raw: raw);
        using var vm = new RestorePreviewViewModel(); vm.SourcePath = path; await Idle(vm);
        vm.Password = Secret; await vm.UnlockAsync();
        var description = vm.Installations[0].Profiles[0].Categories[0].Description;
        Assert.Contains(raw ? "Raw password files" : "Portable password payload", description);
        if (raw) Assert.Contains("not guaranteed", description);
    }

    [Fact]
    public async Task CorruptSourcesAndUnexpectedReaderErrorsAreSanitized()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "bad.bravebackup"); File.WriteAllText(path, "not a zip");
        using var real = new RestorePreviewViewModel(); real.SourcePath = path; await Idle(real);
        Assert.Contains("Unable to validate", real.Status); Assert.Empty(real.Installations);
        using var injected = new RestorePreviewViewModel((_, _, _) => throw new Exception("secret native exception"));
        injected.SourcePath = path; await Idle(injected);
        Assert.DoesNotContain("secret native", injected.Status); Assert.Empty(injected.Installations);
        Assert.Throws<OperationCanceledException>(() => BraveArchiveInspection.Read(path, token: new CancellationToken(true)));
    }

    [Fact]
    public async Task SwitchingFilesDiscardsLateResultsAndClearsPassword()
    {
        var first = CreateArchive(); var second = CreateArchive();
        var entered = new TaskCompletionSource(); using var gate = new ManualResetEventSlim();
        using var vm = new RestorePreviewViewModel((path, password, token) =>
        {
            if (path == first) { entered.SetResult(); gate.Wait(TimeSpan.FromSeconds(10)); }
            return BraveArchiveInspection.Read(path);
        });
        try
        {
            vm.SourcePath = first; await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); vm.Password = "not-retained";
            vm.SourcePath = second; await Idle(vm); var summary = vm.Summary;
            Assert.Empty(vm.Password); Assert.Single(vm.Installations);
            gate.Set(); await Task.Delay(100); Assert.Equal(summary, vm.Summary); Assert.Equal(second, vm.SourcePath);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task PickerResultsAfterLeavingAndInspectionAfterDisposalAreIgnored()
    {
        var selected = new TaskCompletionSource<string?>();
        using var vm = new RestorePreviewViewModel(); vm.PickSource = () => selected.Task;
        var picking = vm.BrowseAsync(); vm.Leave(); selected.SetResult(CreateArchive()); await picking;
        Assert.Empty(vm.SourcePath); Assert.Empty(vm.Installations);
        vm.Password = "not-retained"; vm.Dispose(); Assert.Empty(vm.Password); Assert.Null(vm.PickSource);
        await vm.EnterAsync(); Assert.Empty(vm.Installations);
    }

    [Fact]
    public async Task LeavingRestoreTabClearsStateAndCancelsPendingReader()
    {
        using var shell = new ShellViewModel(() => [], _ => [], _ => new(0, 0));
        shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
        shell.RestorePreview.SourcePath = CreateArchive(); await Idle(shell.RestorePreview);
        Assert.Single(shell.RestorePreview.Installations); shell.RestorePreview.Password = "not-retained";
        shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
        Assert.Empty(shell.RestorePreview.Password); Assert.Empty(shell.RestorePreview.Installations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PendingInspectionCannotRepopulateAfterLeavingOrDisposal(bool dispose)
    {
        var path = CreateArchive(); var result = BraveArchiveInspection.Read(path);
        var entered = new TaskCompletionSource(); var cancelled = new TaskCompletionSource();
        using var gate = new ManualResetEventSlim(); var block = false;
        using var vm = new RestorePreviewViewModel((_, _, token) =>
        {
            if (block)
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.SetResult(); gate.Wait(TimeSpan.FromSeconds(10));
            }
            return result;
        });
        vm.SourcePath = path; await Idle(vm); block = true;
        var operation = vm.EnterAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (dispose) vm.Dispose(); else vm.Leave();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gate.Set(); await operation;
            Assert.Empty(vm.Installations); Assert.Empty(vm.Summary); Assert.False(vm.IsBusy);
        }
        finally { gate.Set(); }
    }

    private static async Task Idle(RestorePreviewViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
