using System.Text.Json;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class TabManagerPlusRestoreIntegrationTests : IDisposable
{
    private const string ArchivePassword = "synthetic-archive-key";
    private const string SafetyPassword = "synthetic-safety-key";
    private const string ExtensionId = SavedWindows.OfficialExtensionId;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TabManagerPlusRestore_" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmRestoresSavedWindowsAndCanResumeOrRollBackAcrossProfiles(bool rollBack)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await CreateSetup();
        var originals = setup.Targets.Select(profile => ReadStore(Store(profile))).ToArray();
        var currentFile = Path.Combine(Store(setup.Targets[0]), "CURRENT");
        const UnixFileMode originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var originalWriteTime = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetUnixFileMode(currentFile, originalMode);
        File.SetLastWriteTimeUtc(currentFile, originalWriteTime);
        var archived = Windows(ReadStore(Fixture("table-and-wal")));
        Assert.Single(archived.Windows);
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        await Map(view, setup);
        Review(view);
        var review = Assert.IsType<RestoreReview>(view.Review);
        Assert.Equal(setup.Targets.Select(Store), review.Targets.Select(target => target.DestinationPath));
        Assert.Contains("An empty backup collection clears the saved windows", view.ReviewSummary);
        Assert.Empty(Directory.GetFileSystemEntries(setup.Recovery));

        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var intercepted = 0;
        var firstStore = Store(setup.Targets[0]);
        view.Planning.ObserveBrowserState = _ =>
        {
            if (Directory.EnumerateFiles(firstStore).Count() > originals[0].Count &&
                Interlocked.Exchange(ref intercepted, 1) == 0)
            {
                boundary.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(25))) return ProcessPresence.Unknown;
            }
            return ProcessPresence.Stopped;
        };

        var forward = view.ConfirmRestoreCommand.ExecuteAsync(null);
        try
        {
            var completed = await Task.WhenAny(boundary.Task, forward).WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(completed == boundary.Task, view.RestoreStatus + " " + view.RestoreErrorCode);
            view.CancelRestoreCommand.Execute(null);
        }
        finally
        {
            release.Set();
            await forward.WaitAsync(TimeSpan.FromSeconds(25));
        }

        Assert.True(view.IsRestorePaused);
        Assert.Equal(50, view.RestoreProgress);
        Assert.Contains("Tab Manager Plus saved windows", view.RestoreReport);
        Assert.Equal(review.SafetyPath, view.PublishedSafetyPath);
        Assert.True(File.Exists(review.SafetyPath));
        Assert.True(File.Exists(review.JournalPath));
        AssertRestored(firstStore, originals[0], archived.Windows);
        AssertOriginal(Store(setup.Targets[1]), originals[1]);
        Assert.Equal(originalMode, File.GetUnixFileMode(currentFile));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(currentFile));
        var newLog = Assert.Single(ReadStore(firstStore).Keys.Except(originals[0].Keys));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(firstStore, newLog)));

        if (rollBack)
        {
            File.Delete(Path.Combine(Package(setup.Targets[1]), "manifest.json"));
            view.DeclineRollbackCommand.Execute(null);
            await view.ResumeRestoreCommand.ExecuteAsync(null);
            Assert.True(view.IsRestorePaused);
            Assert.True(view.IsRollbackPromptVisible);
            Assert.Equal("restore-tabmanagerplus-target", view.RestoreErrorCode);
            AssertOriginal(Store(setup.Targets[1]), originals[1]);

            File.Delete(setup.Archive);
            await view.ConfirmRollbackCommand.ExecuteAsync(null);
            Assert.Equal("Rollback complete.", view.RestoreStatus);
            for (var i = 0; i < setup.Targets.Length; i++) AssertOriginal(Store(setup.Targets[i]), originals[i]);
            Assert.Equal(originalMode, File.GetUnixFileMode(currentFile));
            Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(currentFile));
        }
        else
        {
            view.DeclineRollbackCommand.Execute(null);
            await view.ResumeRestoreCommand.ExecuteAsync(null);
            Assert.Equal("Tab Manager Plus saved windows restored.", view.RestoreStatus);
            for (var i = 0; i < setup.Targets.Length; i++) AssertRestored(Store(setup.Targets[i]), originals[i], archived.Windows);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmRejectsPackageOrStoreChangesBeforeCreatingSafetyArtifacts(bool packageChanged)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await CreateSetup();
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        await Map(view, setup);
        Review(view);
        var target = setup.Targets[0];
        var change = packageChanged ? Path.Combine(Package(target), "changed.js") : Path.Combine(Store(target), "LOG");
        File.WriteAllText(change, "synthetic change");
        var original = ReadStore(Store(target));

        await view.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.Equal("restore-tabmanagerplus-target", view.RestoreErrorCode);
        Assert.Empty(Directory.GetFileSystemEntries(setup.Recovery));
        AssertOriginal(Store(target), original);
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("temporary file")]
    [InlineData("running")]
    public async Task RecheckKeepsUnsupportedDestinationsUnavailable(string problem)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await CreateSetup();
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        var row = view.Planning.Groups.Single().Profiles[0];
        MapProfile(row, setup.Targets[0]);
        if (problem == "registration") File.WriteAllText(Path.Combine(setup.Targets[0].ProfileDirectory, "Preferences"), "{}");
        if (problem == "temporary file") File.WriteAllText(Path.Combine(Store(setup.Targets[0]), "000012.dbtmp"), "");
        if (problem == "running") view.Planning.ObserveBrowserState = _ => ProcessPresence.Running;

        await view.Planning.RecheckAsync();

        Assert.False(Assert.Single(row.Categories).IsSupported);
        if (problem == "running") Assert.Contains("Close Brave", row.CheckResult);
        else Assert.Contains("Unable to verify", row.TabManagerPlusCheckResult);
        Assert.False(view.ReviewRestoreCommand.CanExecute(null));
        Assert.Empty(Directory.GetFileSystemEntries(setup.Recovery));
    }

    [Fact]
    public async Task ReviewRequiresResolvingOverlapWithRawExtensionRestore()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await CreateSetup(includeExtensions: true);
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        await Map(view, setup);
        var row = view.Planning.Groups.Single().Profiles[0];
        var extensions = row.Categories.Single(category => category.Id == "extensions");
        Assert.True(extensions.IsSupported, row.ExtensionCheckResult);
        extensions.IsSelected = true;

        Assert.Contains("same Local store", view.Planning.Availability);
        Assert.False(view.ReviewRestoreCommand.CanExecute(null));
        Assert.Empty(Directory.GetFileSystemEntries(setup.Recovery));

        extensions.IsSelected = false;
        Review(view);
    }

    private async Task<Setup> CreateSetup(bool includeExtensions = false)
    {
        var source = Installation("source");
        var target = Installation("target");
        var sourceProfiles = new[] { Profile(source, "Default", "table-and-wal"), Profile(source, "Profile 1", "table-and-wal") };
        var targets = new[] { Profile(target, "Default", "empty"), Profile(target, "Profile 1", "empty") };
        var archive = Path.Combine(_root, "saved-windows.bravebackup");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        await BraveArchiveBackup.CreateAsync([new(source, sourceProfiles)],
            includeExtensions ? ["extensions", "tabmanagerplus"] : ["tabmanagerplus"], archive, ArchivePassword,
            "balanced", [source.UserDataDirectory], "synthetic", _ => Task.FromResult(false), null, default,
            new ExtensionBackupOptions { ObserveBrowserState = () => ProcessPresence.Stopped }, new SessionBackupOptions(),
            new TabManagerPlusBackupOptions { ObserveBrowserState = () => ProcessPresence.Stopped });
        return new(archive, recovery, target, targets);
    }

    private BraveAppInfo Installation(string name) =>
        new(BraveAppChannel.Stable, "1.96.61", "synthetic", Path.Combine(_root, name));

    private static BraveProfileInfo Profile(BraveAppInfo app, string name, string fixture)
    {
        var path = Path.Combine(app.UserDataDirectory, name);
        var profile = new BraveProfileInfo(name, path, Path.Combine(path, "Bookmarks"));
        Directory.CreateDirectory(Package(profile));
        const string manifest = """{"name":"Synthetic","version":"7.0.0","manifest_version":3}""";
        File.WriteAllText(Path.Combine(Package(profile), "manifest.json"), manifest);
        var registration = new Dictionary<string, object>
        {
            [ExtensionId] = new { location = 1, path = ExtensionId + "/7.0.0_0", manifest = new { version = "7.0.0" } }
        };
        File.WriteAllText(Path.Combine(path, "Preferences"), JsonSerializer.Serialize(new { extensions = new { settings = registration } }));
        Directory.CreateDirectory(Store(profile));
        foreach (var file in Directory.GetFiles(Fixture(fixture)))
            File.Copy(file, Path.Combine(Store(profile), Path.GetFileName(file)));
        return profile;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus", "storage", name);
    private static string Store(BraveProfileInfo profile) => Path.Combine(profile.ProfileDirectory, "Local Extension Settings", ExtensionId);
    private static string Package(BraveProfileInfo profile) => Path.Combine(profile.ProfileDirectory, "Extensions", ExtensionId, "7.0.0_0");

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadStore(string directory) =>
        Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path),
            StringComparer.Ordinal);

    private static SavedWindowCollection Windows(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) =>
        SavedWindows.Parse(OfflineLevelDb.ReadValue(files, "sessions")!, SavedWindows.SupportedVersion);

    private static void AssertOriginal(string directory, Dictionary<string, ReadOnlyMemory<byte>> original)
    {
        var actual = ReadStore(directory);
        Assert.Equal(original.Count, actual.Count);
        foreach (var (name, bytes) in original) Assert.Equal(bytes.ToArray(), actual[name].ToArray());
    }

    private static void AssertRestored(string directory, Dictionary<string, ReadOnlyMemory<byte>> original, SavedWindow[] expected)
    {
        var actual = ReadStore(directory);
        Assert.EndsWith(".log", Assert.Single(actual.Keys.Except(original.Keys)));
        foreach (var (name, bytes) in original) Assert.Equal(bytes.ToArray(), actual[name].ToArray());
        var restored = Assert.Single(Windows(actual).Windows);
        var archived = Assert.Single(expected);
        Assert.Equal(archived.Id, restored.Id);
        Assert.Equal(archived.Name, restored.Name);
        Assert.Equal(archived.Tabs, restored.Tabs);
        var unrelated = OfflineLevelDb.ReadValue(original, "fixture-padding");
        Assert.NotNull(unrelated);
        Assert.Equal(unrelated, OfflineLevelDb.ReadValue(actual, "fixture-padding"));
    }

    private static async Task<ShellViewModel> OpenRestore(Setup setup)
    {
        var shell = new ShellViewModel(() => [setup.Installation], _ => setup.Targets, _ => new BraveProfileSummary(0, 0));
        try
        {
            await shell.RefreshAsync();
            shell.Activate(Assert.Single(shell.Installations));
            shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
            var view = shell.RestorePreview;
            view.SourcePath = setup.Archive;
            await view.EnterAsync();
            Assert.True(view.NeedsUnlock);
            view.Password = ArchivePassword;
            await view.UnlockAsync();
            Assert.False(view.NeedsUnlock);
            view.Planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
            return shell;
        }
        catch { shell.Dispose(); throw; }
    }

    private static async Task Map(RestorePreviewViewModel view, Setup setup)
    {
        var rows = view.Planning.Groups.Single().Profiles;
        for (var i = 0; i < rows.Length; i++) MapProfile(rows[i], setup.Targets[i]);
        await view.Planning.RecheckAsync();
        foreach (var row in rows)
        {
            var choice = row.Categories.Single(category => category.Id == "tabmanagerplus");
            Assert.True(choice.IsSupported, row.TabManagerPlusCheckResult);
            Assert.False(choice.IsSelected);
            choice.IsSelected = true;
        }
        view.Safety.Folder = setup.Recovery;
        view.Safety.Password = SafetyPassword;
        view.Safety.Confirmation = SafetyPassword;
    }

    private static void MapProfile(RestoreProfileChoice row, BraveProfileInfo target)
    {
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = row.TargetProfiles.Single(profile => profile.Info == target);
    }

    private static void Review(RestorePreviewViewModel view)
    {
        Assert.True(view.ReviewRestoreCommand.CanExecute(null), view.Planning.Availability);
        view.ReviewRestoreCommand.Execute(null);
        Assert.True(view.ConfirmRestoreCommand.CanExecute(null), view.ReviewError);
    }

    private sealed record Setup(string Archive, string Recovery, BraveAppInfo Installation, BraveProfileInfo[] Targets);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
