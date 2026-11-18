using System.Text.Json;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ExtensionRestoreAssessmentTests : IDisposable
{
    private const string Version = "1.96.61";
    private const string Password = "synthetic-archive-key";
    private const string StorageMarker = "synthetic opaque store payload";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExtensionRestore_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecheckDistinguishesRegistrationVersionsAndPackageContentsWithoutWriting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateProfile("source");
        foreach (var identity in "abcdfgh")
            WritePackage(source.Profile.ProfileDirectory, identity, "10.0");
        WritePackage(source.Profile.ProfileDirectory, 'a', "9.0");
        WriteStorage(source.Profile.ProfileDirectory, 'a');
        WriteStorage(source.Profile.ProfileDirectory, 'e');
        Directory.CreateDirectory(Path.Combine(source.Profile.ProfileDirectory, "Sync Extension Settings", Id('a')));

        var target = CreateProfile("target");
        var targetPath = target.Profile.ProfileDirectory;
        foreach (var identity in "abcd")
            WritePackage(targetPath, identity, "10.0", revision: 1, script: identity == 'c' ? "// Y" : "// X");
        Directory.CreateDirectory(Path.Combine(targetPath, "Extensions", Id('h')));
        Directory.CreateSymbolicLink(Path.Combine(targetPath, "Extensions", Id('h'), "10.0_0"),
            Path.Combine(source.Profile.ProfileDirectory, "Extensions", Id('h'), "10.0_0"));

        // Chromium's internal package registration records relative paths under Extensions.
        WriteRegistrations(targetPath, "Preferences", new()
        {
            [Id('a')] = Registration('a', "10.0", revision: 1),
            [Id('c')] = Registration('c', "10.0", revision: 1),
            [Id('d')] = Registration('d', "11.0"),
            [Id('f')] = Registration('f', "10.0", path: "../../outside"),
            [Id('g')] = Registration('g', "10.0"),
            [Id('h')] = Registration('h', "10.0")
        });
        WriteRegistrations(targetPath, "Secure Preferences", new() { [Id('g')] = Registration('g', "10.0") });
        var original = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var archive = await CreateArchive(source);
        var archiveBytes = File.ReadAllBytes(archive);
        using var shell = await OpenRestore(archive, target);
        var restore = shell.RestorePreview;
        var category = Assert.Single(Assert.Single(restore.Installations[0].Profiles).Categories);
        Assert.Contains("8 archived extension identities", category.Description);
        var row = Assert.Single(Assert.Single(restore.Planning.Groups).Profiles);
        Assert.False(Assert.Single(row.Categories).IsSupported);

        await MapAndRecheck(restore.Planning, row);

        var results = row.ExtensionCheckResult.Split('\n');
        Assert.Contains("1 of 8", results[0]);
        CheckLine('a', "Registered version 10.0 matches the archived package", "Local present, Sync empty");
        CheckLine('b', "No destination registration");
        CheckLine('c', "Registered version 10.0 differs from the archived package");
        CheckLine('d', "Registered version 11.0 is absent from the archive");
        CheckLine('e', "Archive contains storage only", "Local present, Sync absent");
        CheckLine('f', "Registration uses an unsupported location or format");
        CheckLine('g', "Registration or package could not be verified");
        CheckLine('h', "Registration or package could not be verified");
        Assert.DoesNotContain(StorageMarker, row.ExtensionCheckResult);
        Assert.False(restore.ReviewRestoreCommand.CanExecute(null));
        Assert.False(restore.ConfirmRestoreCommand.CanExecute(null));
        Assert.Equal(archiveBytes, File.ReadAllBytes(archive));
        Assert.Equal(original.Keys.Order(), Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories).Order());
        foreach (var (file, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(file));

        void CheckLine(char id, params string[] fragments)
        {
            var line = Assert.Single(results.Where(result => result.StartsWith(Id(id), StringComparison.Ordinal)));
            foreach (var fragment in fragments) Assert.Contains(fragment, line);
        }
    }

    [Fact]
    public async Task RecheckSkipsOpenBrowserAndClearsStaleDestinationResults()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateProfile("source");
        WritePackage(source.Profile.ProfileDirectory, 'a', "10.0");
        var target = CreateProfile("target");
        WritePackage(target.Profile.ProfileDirectory, 'a', "10.0");
        WriteRegistrations(target.Profile.ProfileDirectory, "Secure Preferences", new() { [Id('a')] = Registration('a', "10.0") });
        var archive = await CreateArchive(source);
        using var shell = await OpenRestore(archive, target);
        var planning = shell.RestorePreview.Planning;
        var row = Assert.Single(Assert.Single(planning.Groups).Profiles);
        await MapAndRecheck(planning, row);
        Assert.Contains("1 of 1", row.ExtensionCheckResult);

        var observations = 0;
        planning.ObserveBrowserState = _ => observations++ == 0 ? ProcessPresence.Stopped : ProcessPresence.Running;
        await planning.RecheckAsync();
        Assert.Contains("stayed closed", row.CheckResult);
        Assert.Empty(row.ExtensionCheckResult);
        planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
        await planning.RecheckAsync();
        Assert.Contains("1 of 1", row.ExtensionCheckResult);

        row.SelectedProfile = null;
        Assert.Empty(row.ExtensionCheckResult);
        row.SelectedProfile = Assert.Single(row.TargetProfiles);
        planning.ObserveBrowserState = _ => ProcessPresence.Running;
        await planning.RecheckAsync();
        Assert.Contains("Running", row.CheckResult);
        Assert.Empty(row.ExtensionCheckResult);
    }

    [Fact]
    public async Task CompatibilityRequiresMatchingBrowserWithoutBlockingArchiveInspection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateProfile("source");
        WritePackage(source.Profile.ProfileDirectory, 'a', "10.0");
        var target = CreateProfile("target");
        var archive = await CreateArchive(source);
        using var shell = await OpenRestore(archive, (target.App with { Version = "1.96.60" }, target.Profile));
        var planning = shell.RestorePreview.Planning;
        var row = Assert.Single(Assert.Single(planning.Groups).Profiles);
        await MapAndRecheck(planning, row);
        Assert.Contains("same recorded Brave product and version", row.ExtensionCheckResult);
        Assert.False(Assert.Single(row.Categories).IsSupported);
    }

    private (BraveAppInfo App, BraveProfileInfo Profile) CreateProfile(string folder)
    {
        var root = Path.Combine(_root, folder);
        var profile = Directory.CreateDirectory(Path.Combine(root, "Default")).FullName;
        File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");
        return (new(BraveAppChannel.Stable, Version, "synthetic", root),
            new("Synthetic", profile, Path.Combine(profile, "Bookmarks")));
    }

    private static string Id(char character) => new(character, 32);

    private static void WritePackage(string profile, char identity, string version, int revision = 0, string script = "// X")
    {
        var directory = Directory.CreateDirectory(Path.Combine(profile, "Extensions", Id(identity), $"{version}_{revision}")).FullName;
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new { name = "Synthetic package", version, manifest_version = 3 }));
        File.WriteAllText(Path.Combine(directory, "script.js"), script);
    }

    private static void WriteStorage(string profile, char identity)
    {
        var directory = Directory.CreateDirectory(Path.Combine(profile, "Local Extension Settings", Id(identity))).FullName;
        File.WriteAllText(Path.Combine(directory, "data.test"), StorageMarker);
    }

    private static object Registration(char identity, string version, int revision = 0, string? path = null) =>
        new { location = 1, path = path ?? $"{Id(identity)}/{version}_{revision}", manifest = new { version } };

    private static void WriteRegistrations(string profile, string store, Dictionary<string, object> settings) =>
        File.WriteAllText(Path.Combine(profile, store), JsonSerializer.Serialize(new { extensions = new { settings } }));

    private async Task<string> CreateArchive((BraveAppInfo App, BraveProfileInfo Profile) source)
    {
        var archive = Path.Combine(_root, "extensions.bravebackup");
        await BraveArchiveBackup.CreateAsync([new(source.App, [source.Profile])], ["extensions"], archive,
            Password, "balanced", [source.App.UserDataDirectory], "synthetic", _ => Task.FromResult(false),
            progress: null, cancellationToken: default,
            extensionOptions: new ExtensionBackupOptions { ObserveBrowserState = () => ProcessPresence.Stopped });
        return archive;
    }

    private static async Task<ShellViewModel> OpenRestore(string archive, (BraveAppInfo App, BraveProfileInfo Profile) target)
    {
        var shell = new ShellViewModel(() => [target.App], _ => [target.Profile], _ => new BraveProfileSummary(0, 0));
        try
        {
            await shell.RefreshAsync();
            shell.Activate(Assert.Single(shell.Installations));
            shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
            var restore = shell.RestorePreview;
            restore.SourcePath = archive;
            await restore.EnterAsync();
            Assert.True(restore.NeedsUnlock);
            restore.Password = Password;
            await restore.UnlockAsync();
            Assert.Empty(restore.Password);
            return shell;
        }
        catch { shell.Dispose(); throw; }
    }

    private static async Task MapAndRecheck(RestorePlanningViewModel planning, RestoreProfileChoice row)
    {
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = Assert.Single(row.TargetProfiles);
        planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
        await planning.RecheckAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
