using System.IO.Compression;
using System.Text;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestorePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestorePreview_" + Guid.NewGuid().ToString("N"));
    private const string Secret = "synthetic-unlock-key";
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private string CreateArchive(int version = 8, bool encrypted = false, bool raw = false, bool missingMain = false, byte[]? portable = null)
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
            var rawIndex = $$"""
                {"schemaVersion":1,"captureKind":"raw-native-password-files","portableCaptureCompleted":false,"reasonCode":"passwords-database-invalid","missingMainDatabases":[{{(missingMain ? "\"Login Data\"" : string.Empty)}}]}
                """;
            ArchiveFile[] files = raw ?
            [BackupArchive.WriteFile(zip, prefix + "raw-index-v1.json", "raw-password-index", Encoding.UTF8.GetBytes(rawIndex), "balanced"),
             BackupArchive.WriteFile(zip, prefix + (missingMain ? "raw/Login Data-wal" : "raw/Login Data"),
                 "native-password-file", "synthetic"u8.ToArray(), "balanced")] :
            [BackupArchive.WriteFile(zip, prefix + (version == 8 ? "passwords.csv" : "portable-v1.bin"),
                version == 8 ? "plaintext-password-csv" : "portable-passwords",
                version == 8 ? "name,url,username,password,note\nSynthetic,https://example.test/,fixture-user,synthetic-secret,\n"u8.ToArray() :
                    portable ?? File.ReadAllBytes(Path.Combine(Fixtures, "PortablePasswords", "Payload.v1.synthetic.bin")), "balanced")];
            var category = new ArchiveCategory("passwords", version - 5, files);
            BackupArchive.WriteManifest(zip, new("BraveBackupArchive", version, Guid.NewGuid(), DateTimeOffset.UtcNow, "synthetic-app",
                ["passwords-v" + category.Version], "balanced", [new(installation, portable is null ? "Brave" : "Browser", "Stable", "1.102.13", [new(profile, "Synthetic profile", "Default", [category])])]));
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
        Assert.Contains("1 CSV row", category.Description); Assert.DoesNotContain("fixture-user", category.ToString());
        Assert.DoesNotContain("synthetic-secret", vm.Summary + vm.Status + category.ToString()); Assert.Empty(vm.Password);
        vm.Leave(); Assert.Empty(vm.Installations); Assert.Empty(vm.Summary); Assert.Empty(vm.Password);
    }

    [Theory]
    [InlineData(6, false, false, "Portable password payload", "Profile store contains 1 saved login and 2 notes.")]
    [InlineData(7, false, false, "Portable password payload", "Local account store contains 0 saved logins and 0 notes.")]
    [InlineData(7, true, false, "Raw password files", "1 native file preserved.")]
    [InlineData(7, true, true, "Raw password files", "1 main database missing.")]
    [InlineData(8, false, false, "Password CSV", "1 CSV row.")]
    public async Task PasswordInventoryReportsOnlyValidatedCountsAndKeepsRestoreDisabled(
        int version, bool raw, bool missingMain, string representation, string expected)
    {
        var path = CreateArchive(version, raw: raw, missingMain: missingMain);
        using var vm = new RestorePreviewViewModel();
        vm.SourcePath = path;
        await Idle(vm);
        if (version < 8)
        {
            Assert.True(vm.NeedsUnlock);
            Assert.Empty(vm.Installations);
            vm.Password = Secret;
            await vm.UnlockAsync();
        }

        var preview = Assert.Single(Assert.Single(Assert.Single(vm.Installations).Profiles).Categories);
        Assert.Equal("Passwords", preview.Label);
        Assert.Contains(representation, preview.Description);
        Assert.Contains(expected, preview.Description);
        Assert.Contains("Password restore is unavailable", preview.Description);
        if (version is 6 or 7 && !raw)
            Assert.Contains("Excluded 1 blocklisted entry and 2 federated entries", preview.Description);

        var data = Path.Combine(_root, "target");
        var directory = Directory.CreateDirectory(Path.Combine(data, "Default")).FullName;
        var loginDatabase = Path.Combine(directory, "Login Data");
        File.WriteAllText(loginDatabase, "synthetic destination sentinel");
        vm.Planning.SetTargets([new(new(BraveAppChannel.Stable, "1.102.13", "synthetic", data),
            [new(new("Default", directory, Path.Combine(directory, "Bookmarks")))])]);
        vm.Planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
        var row = Assert.Single(Assert.Single(vm.Planning.Groups).Profiles);
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = Assert.Single(row.TargetProfiles);

        await vm.Planning.RecheckAsync();
        row.SelectAllSupportedCommand.Execute(null);

        var choice = Assert.Single(row.Categories);
        Assert.False(choice.IsSupported);
        Assert.False(choice.IsSelected);
        Assert.False(vm.ReviewRestoreCommand.CanExecute(null));
        Assert.Contains(representation, choice.SupportReason);
        Assert.Contains("Password restore is unavailable", choice.SupportReason);
        var visible = preview.Description + choice.SupportReason + vm.Summary + vm.Status;
        foreach (var value in new[] { "synthetic.invalid", "example.test", "fixture-user", "synthetic-secret", "synthetic note" })
            Assert.DoesNotContain(value, visible);
        Assert.Equal("synthetic destination sentinel", File.ReadAllText(loginDatabase));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task TemporaryPasswordLoginRequiresCompatibleMappingAndIsClearedOnRemap()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = new PortablePasswordDocument([new(PortablePasswordStoreKind.Profile, 0, 0, [])]);
        using var payload = PasswordPayloadFormat.Encode(document);
        var path = CreateArchive(6, portable: payload.Bytes.ToArray());
        var data = Path.Combine(_root, "target");
        var first = Directory.CreateDirectory(Path.Combine(data, "Default")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(data, "Profile 1")).FullName;

        using var vm = new RestorePreviewViewModel();
        vm.SourcePath = path;
        await Idle(vm);
        vm.Password = Secret;
        await vm.UnlockAsync();
        vm.Planning.SetTargets([new(new(BraveAppChannel.Stable, "1.102.13", "synthetic", data),
            [new(new("Default", first, Path.Combine(first, "Bookmarks"))),
             new(new("Profile 1", second, Path.Combine(second, "Bookmarks")))])]);
        vm.Planning.ObserveBrowserState = _ => ProcessPresence.Running;
        var row = Assert.Single(Assert.Single(vm.Planning.Groups).Profiles);

        Assert.False(row.CanVerifyPasswords);
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = row.TargetProfiles[0];
        Assert.True(row.CanVerifyPasswords);
        row.GeneratePasswordVerificationCommand.Execute(null);
        var firstInstructions = row.PasswordVerificationInstructions;
        Assert.Contains("bravebackup-restore.invalid", firstInstructions);
        await vm.Planning.RecheckAsync();
        Assert.Contains("Running", row.CheckResult);
        Assert.False(Assert.Single(row.Categories).IsSupported);
        Assert.Empty(Directory.GetFiles(first));

        row.SelectedProfile = row.TargetProfiles[1];
        Assert.Empty(row.PasswordVerificationInstructions);
        row.GeneratePasswordVerificationCommand.Execute(null);
        Assert.NotEqual(firstInstructions, row.PasswordVerificationInstructions);
        Assert.Empty(Directory.GetFiles(second));
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
