using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class NativeSessionRestoreTests : IDisposable
{
    private const string Version = "1.96.61";
    private const string ArchivePassword = "synthetic-archive-key";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NativeSessionRestore_" + Guid.NewGuid().ToString("N"));
    private static byte[] Archived => BraveSessionDecoderTests.Fixture();
    private static byte[] Original => BraveSessionDecoderTests.Fixture("Session.empty-v3.bin");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmReplacesOnlyReviewedNativeSnapshotAndSupportsResumeOrRollback(bool rollback)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await SetUp();
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        await MapAndReview(view, setup);
        var review = Assert.IsType<RestoreReview>(view.Review);
        Assert.Equal(setup.Targets.Select(SessionFile), review.Targets.Select(row => row.DestinationPath));
        Assert.All(setup.Targets, target => Assert.Equal(Original, File.ReadAllBytes(SessionFile(target))));
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        Assert.DoesNotContain("https://", view.ReviewSummary);

        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var intercepted = 0;
        view.Planning.ObserveBrowserState = _ =>
        {
            if (File.ReadAllBytes(SessionFile(setup.Targets[0])).AsSpan().SequenceEqual(Archived) &&
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
            await boundary.Task.WaitAsync(TimeSpan.FromSeconds(25));
            view.CancelRestoreCommand.Execute(null);
        }
        finally
        {
            release.Set();
            await forward.WaitAsync(TimeSpan.FromSeconds(25));
        }

        Assert.True(view.IsRestorePaused);
        Assert.Equal(50, view.RestoreProgress);
        Assert.Equal(Archived, File.ReadAllBytes(SessionFile(setup.Targets[0])));
        Assert.Equal(Original, File.ReadAllBytes(SessionFile(setup.Targets[1])));
        Assert.Equal(review.SafetyPath, view.PublishedSafetyPath);

        if (rollback)
        {
            var later = Path.Combine(setup.Targets[1].ProfileDirectory, "Sessions", "Session_20");
            File.WriteAllBytes(later, Original);
            view.DeclineRollbackCommand.Execute(null);
            await view.ResumeRestoreCommand.ExecuteAsync(null);
            Assert.True(view.IsRestorePaused);
            Assert.True(view.IsRollbackPromptVisible);
            Assert.Equal("restore-session-target", view.RestoreErrorCode);
            File.Delete(setup.Archive);
            await view.ConfirmRollbackCommand.ExecuteAsync(null);
            Assert.Equal("Rollback complete.", view.RestoreStatus);
            Assert.Equal(Original, File.ReadAllBytes(later));
            Assert.All(setup.Targets, target => Assert.Equal(Original, File.ReadAllBytes(SessionFile(target))));
        }
        else
        {
            view.DeclineRollbackCommand.Execute(null);
            await view.ResumeRestoreCommand.ExecuteAsync(null);
            Assert.Equal("Persisted Sessions restored.", view.RestoreStatus);
            Assert.All(setup.Targets, target => Assert.Equal(Archived, File.ReadAllBytes(SessionFile(target))));
        }

        Assert.All(setup.Targets, target =>
        {
            Assert.Equal("older session", File.ReadAllText(Path.Combine(target.ProfileDirectory, "Sessions", "Session_2")));
            Assert.Equal("closed tabs", File.ReadAllText(Path.Combine(target.ProfileDirectory, "Sessions", "Tabs_30")));
            Assert.Equal("legacy tabs", File.ReadAllText(Path.Combine(target.ProfileDirectory, "Last Tabs")));
        });
        Assert.True(File.Exists(review.SafetyPath));
        Assert.DoesNotContain("https://", view.RestoreReport);
    }

    [Theory]
    [InlineData("portable", "portable session only")]
    [InlineData("encrypted", "encrypted session files")]
    [InlineData("malformed", "not a supported complete")]
    [InlineData("ambiguous", "unsafe or ambiguous")]
    public async Task RecheckKeepsUnsupportedArchivesOrDestinationsReadOnly(string condition, string reason)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await SetUp(includeNative: condition != "portable");
        var first = setup.Targets[0];
        if (condition == "encrypted")
        {
            var encrypted = Directory.CreateDirectory(Path.Combine(first.ProfileDirectory, "Sessions_Encrypted")).FullName;
            File.WriteAllBytes(Path.Combine(encrypted, "Session_12"), BraveSessionDecoderTests.Fixture("Session.encrypted-header.bin"));
        }
        if (condition == "malformed") File.WriteAllText(SessionFile(first), "invalid");
        if (condition == "ambiguous") File.WriteAllBytes(Path.Combine(first.ProfileDirectory, "Sessions", "Session_010"), Original);

        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        var row = view.Planning.Groups.Single().Profiles[0];
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = row.TargetProfiles[0];
        await view.Planning.RecheckAsync();

        Assert.Contains(reason, row.SessionCheckResult);
        Assert.False(Assert.Single(row.Categories).IsSupported);
        Assert.False(view.ConfirmRestoreCommand.CanExecute(null));
        Assert.Empty(Directory.GetFiles(setup.Recovery));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmRejectsChangedSnapshotOrNewerSlotAfterReview(bool newer)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await SetUp();
        using var shell = await OpenRestore(setup);
        var view = shell.RestorePreview;
        await MapAndReview(view, setup);
        var current = SessionFile(setup.Targets[0]);
        var edited = newer ? Path.Combine(Path.GetDirectoryName(current)!, "Session_20") : current;
        File.WriteAllBytes(edited, Archived);

        await view.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.Equal("restore-session-target", view.RestoreErrorCode);
        Assert.Equal(Archived, File.ReadAllBytes(edited));
        Assert.Equal(Original, File.ReadAllBytes(SessionFile(setup.Targets[1])));
        Assert.Empty(Directory.GetFiles(setup.Recovery));
    }

    private async Task<Setup> SetUp(bool includeNative = true)
    {
        var source = Installation("source");
        var sourceProfiles = new[] { Profile(source, "Default", Archived), Profile(source, "Profile 1", Archived) };
        var target = Installation("target");
        var targets = new[] { Profile(target, "Default", Original), Profile(target, "Profile 1", Original) };
        var archive = Path.Combine(_root, "sessions.bravebackup");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        await BraveArchiveBackup.CreateAsync([new(source, sourceProfiles)], ["sessions"], archive, ArchivePassword,
            "balanced", [source.UserDataDirectory], "synthetic", _ => Task.FromResult(false), null, default,
            new ExtensionBackupOptions(), new SessionBackupOptions { IncludeNativeFiles = includeNative });
        return new(archive, recovery, target, targets);
    }

    private BraveAppInfo Installation(string name) => new(BraveAppChannel.Stable, Version, "synthetic", Path.Combine(_root, name));

    private static BraveProfileInfo Profile(BraveAppInfo app, string name, byte[] snapshot)
    {
        var profile = Path.Combine(app.UserDataDirectory, name);
        var sessions = Directory.CreateDirectory(Path.Combine(profile, "Sessions")).FullName;
        File.WriteAllBytes(Path.Combine(sessions, "Session_10"), snapshot);
        if (app.UserDataDirectory.EndsWith("target", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(sessions, "Session_2"), "older session");
            File.WriteAllText(Path.Combine(sessions, "Tabs_30"), "closed tabs");
            File.WriteAllText(Path.Combine(profile, "Last Tabs"), "legacy tabs");
        }
        else
        {
            File.WriteAllBytes(Path.Combine(sessions, "Session_2"), Original);
        }
        return new(name, profile, Path.Combine(profile, "Bookmarks"));
    }

    private static string SessionFile(BraveProfileInfo target) => Path.Combine(target.ProfileDirectory, "Sessions", "Session_10");

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

    private static async Task MapAndReview(RestorePreviewViewModel view, Setup setup)
    {
        var rows = view.Planning.Groups.Single().Profiles;
        foreach (var row in rows)
        {
            row.IsSelected = true;
            row.SelectedInstallation = Assert.Single(row.TargetInstallations);
            row.SelectedProfile = row.TargetProfiles.Single(profile => row.Name.StartsWith(profile.Name + " (", StringComparison.Ordinal));
        }
        await view.Planning.RecheckAsync();
        foreach (var row in rows)
        {
            var choice = Assert.Single(row.Categories);
            Assert.True(choice.IsSupported, row.SessionCheckResult);
            Assert.False(choice.IsSelected);
            choice.IsSelected = true;
        }
        view.Safety.Folder = setup.Recovery;
        view.Safety.Password = "synthetic-safety-key";
        view.Safety.Confirmation = view.Safety.Password;
        Assert.True(view.ReviewRestoreCommand.CanExecute(null));
        view.ReviewRestoreCommand.Execute(null);
        Assert.True(view.ConfirmRestoreCommand.CanExecute(null));
    }

    private sealed record Setup(string Archive, string Recovery, BraveAppInfo Installation, BraveProfileInfo[] Targets);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
