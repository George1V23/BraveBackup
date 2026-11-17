using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestorePlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestorePlanning_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source.bravebackup");
    private RestoreTargetInstallation Target(string installation, string profile = "Default")
    {
        var data = Path.Combine(_root, installation); var directory = Path.Combine(data, profile);
        Directory.CreateDirectory(directory);
        return new(new(BraveAppChannel.Stable, "1.102.13", "synthetic", data),
            [new(new("Same name", directory, Path.Combine(directory, "Bookmarks")))]);
    }
    private static ArchiveManifest Manifest() => new("BraveBackupArchive", 2, Guid.NewGuid(), DateTimeOffset.UtcNow,
        "synthetic", ["bookmarks-v1", "settings-v1"], "balanced",
        [new(Guid.NewGuid(), "Brave", "Stable", "1.102.13", Enumerable.Range(0, 2).Select(_ => new ArchiveProfile(
            Guid.NewGuid(), "Same name", "Default", [new("bookmarks", 1, []), new("settings", 1, [])])).ToArray())]);
    private RestorePlanningViewModel Create()
    {
        var planning = new RestorePlanningViewModel(); planning.SetTargets([Target("browser-a"), Target("browser-b")]);
        File.WriteAllText(Source, "synthetic archive metadata source"); planning.Load(Manifest(), Source); return planning;
    }

    [Fact]
    public void InitiallyUnselectedSourcesAndUnsupportedCategoriesStayIndependent()
    {
        using var planning = Create(); var rows = planning.Groups.Single().Profiles;
        Assert.All(rows, row => { Assert.False(row.IsSelected); Assert.Null(row.SelectedInstallation); Assert.Null(row.SelectedProfile); });
        planning.SelectAllProfilesCommand.Execute(null); Assert.All(rows, row => Assert.True(row.IsSelected));
        Assert.NotEqual(rows[0].SourceId, rows[1].SourceId);
        foreach (var category in rows[0].Categories)
        { Assert.False(category.IsSupported); category.IsSelected = true; Assert.False(category.IsSelected); Assert.NotEmpty(category.SupportReason); }
        rows[0].SelectAllSupportedCommand.Execute(null); Assert.All(rows[0].Categories, category => Assert.False(category.IsSelected));
        planning.ClearProfilesCommand.Execute(null); Assert.All(rows, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void ExplicitMappingShowsFullPathAndInstallationChangeClearsProfile()
    {
        using var planning = Create(); var row = planning.Groups[0].Profiles[0]; row.IsSelected = true;
        row.SelectedInstallation = row.TargetInstallations[0]; Assert.Null(row.SelectedProfile);
        row.SelectedProfile = row.TargetProfiles[0]; Assert.Equal(row.SelectedProfile.FullPath, row.DestinationPath);
        Assert.Contains(row.DestinationPath, planning.PlanSummary); Assert.Contains("Planning only", planning.Availability);
        row.SelectedInstallation = row.TargetInstallations[1]; Assert.Null(row.SelectedProfile);
        Assert.Contains("Choose a destination", planning.Availability);
    }

    [Fact]
    public void DuplicateNormalizedTargetsAndForeignProfileMappingsAreRejected()
    {
        using var planning = Create(); var rows = planning.Groups[0].Profiles;
        foreach (var row in rows) { row.IsSelected = true; row.SelectedInstallation = row.TargetInstallations[0]; row.SelectedProfile = row.TargetProfiles[0]; }
        Assert.Contains("same destination", planning.Availability);
        var alias = Target("browser-a", "sub/../Default");
        planning.SetTargets([Target("browser-a"), alias]); rows = planning.Groups[0].Profiles;
        for (var index = 0; index < rows.Length; index++)
        { rows[index].IsSelected = true; rows[index].SelectedInstallation = rows[index].TargetInstallations[index]; rows[index].SelectedProfile = rows[index].TargetProfiles[0]; }
        Assert.Contains("same destination", planning.Availability);
        rows[1].SelectedProfile = Target("foreign").Profiles[0]; Assert.Contains("no longer part", planning.Availability);
    }

    [Theory]
    [InlineData(ProcessPresence.Stopped, "Closed")]
    [InlineData(ProcessPresence.Running, "Running")]
    [InlineData(ProcessPresence.Unknown, "Unable to verify")]
    public async Task RecheckReportsRealObservationWithoutWrites(ProcessPresence state, string label)
    {
        using var planning = Create(); var row = planning.Groups[0].Profiles[0];
        row.IsSelected = true; row.SelectedInstallation = row.TargetInstallations[0]; row.SelectedProfile = row.TargetProfiles[0];
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        planning.ObserveBrowserState = _ => state; await planning.RecheckAsync();
        Assert.Contains(label, row.CheckResult); Assert.Contains(label, planning.PlanSummary);
        Assert.Contains("restoration is unavailable", planning.Availability);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(before.Count, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task UnsafeOrMissingDestinationAndObserverFailuresStayBlocked()
    {
        using var planning = Create(); var row = planning.Groups[0].Profiles[0];
        row.IsSelected = true; row.SelectedInstallation = row.TargetInstallations[0]; row.SelectedProfile = row.TargetProfiles[0];
        planning.ObserveBrowserState = _ => throw new InvalidOperationException("secret error detail");
        await planning.RecheckAsync(); Assert.Contains("Unable to verify", row.CheckResult); Assert.DoesNotContain("secret", row.CheckResult);
        Directory.Delete(row.SelectedProfile.FullPath); await planning.RecheckAsync(); Assert.Contains("Unable to verify", row.CheckResult);
    }

    [Fact]
    public void GuardRejectsForeignProfilesSourceOverlapAndLinkedDirectories()
    {
        var target = Target("browser"); var profile = target.Profiles[0].Info;
        Assert.Equal(Path.GetFullPath(profile.ProfileDirectory), BraveRestoreTargetGuard.Validate(target.Info, profile, Source));
        Assert.Throws<InvalidDataException>(() => BraveRestoreTargetGuard.Validate(target.Info, Target("foreign").Profiles[0].Info, Source));
        Assert.Throws<InvalidDataException>(() => BraveRestoreTargetGuard.Validate(target.Info, profile, Path.Combine(target.Info.UserDataDirectory, "source.bravebackup")));
        if (OperatingSystem.IsLinux())
        {
            var linked = Path.Combine(target.Info.UserDataDirectory, "Linked"); Directory.CreateSymbolicLink(linked, profile.ProfileDirectory);
            Assert.Throws<InvalidDataException>(() => BraveRestoreTargetGuard.Validate(target.Info, new("Linked", linked, "unused"), Source));
        }
    }

    [Fact]
    public async Task RediscoveryAndLateRechecksCannotRepopulateOldMappings()
    {
        using var planning = Create(); var row = planning.Groups[0].Profiles[0];
        row.IsSelected = true; row.SelectedInstallation = row.TargetInstallations[0]; row.SelectedProfile = row.TargetProfiles[0];
        var entered = new TaskCompletionSource(); using var release = new ManualResetEventSlim();
        planning.ObserveBrowserState = _ => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return ProcessPresence.Stopped; };
        var checking = planning.RecheckAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            planning.SetTargets([Target("replacement")]); release.Set(); await checking;
            Assert.All(planning.Groups[0].Profiles, profile => { Assert.False(profile.IsSelected); Assert.Null(profile.SelectedProfile); });
            Assert.False(planning.IsChecking); Assert.DoesNotContain("Closed", planning.PlanSummary);
            planning.Reset(); Assert.Empty(planning.Groups); Assert.False(planning.HasSource);
        }
        finally { release.Set(); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
