using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestoreExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestoreExecution_" + Guid.NewGuid().ToString("N"));
    private const string Version = "1.102.13";
    private const string ArchivePassword = "synthetic-archive-password";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmPausesAtBoundaryAndRequiresExplicitResumeOrRollback(bool rollBack)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await PrepareReview();
        using var shell = setup.Shell;
        var view = shell.RestorePreview;
        var review = Assert.IsType<RestoreReview>(view.Review);
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        Assert.Equal(setup.OriginalFirst, File.ReadAllBytes(setup.Targets[0].BookmarksPath));

        var nextCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var intercept = 0;
        view.Planning.ObserveBrowserState = _ =>
        {
            if (Volatile.Read(ref intercept) == 0 && File.ReadAllBytes(setup.Targets[0].BookmarksPath).AsSpan().SequenceEqual(setup.Native)
                && Interlocked.Exchange(ref intercept, 1) == 0)
            {
                nextCheck.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(25))) return ProcessPresence.Unknown;
            }
            return ProcessPresence.Stopped;
        };

        var forward = view.ConfirmRestoreCommand.ExecuteAsync(null);
        try
        {
            await nextCheck.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(view.CancelRestoreCommand.CanExecute(null));
            Assert.False(shell.IsWorkspaceEnabled);
            shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
            Assert.Equal(ShellViewModel.RestoreTabIndex, shell.SelectedTabIndex);
            view.CancelRestoreCommand.Execute(null);
        }
        finally
        {
            release.Set();
            await forward.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(view.IsRestorePaused);
        Assert.True(view.IsRollbackPromptVisible);
        Assert.True(view.HasProgressTotal);
        Assert.Equal(50, view.RestoreProgress);
        Assert.Contains("Bookmarks — Restored", view.RestoreReport);
        Assert.Contains("Bookmarks — Pending", view.RestoreReport);
        Assert.False(view.ResumeRestoreCommand.CanExecute(null));
        Assert.Equal(review.SafetyPath, view.PublishedSafetyPath);
        Assert.Equal(setup.Native, File.ReadAllBytes(setup.Targets[0].BookmarksPath));
        Assert.Equal(setup.OriginalSecond, File.ReadAllBytes(setup.Targets[1].BookmarksPath));
        var safety = File.ReadAllBytes(review.SafetyPath);
        Assert.Empty(view.Safety.Password);
        Assert.Empty(view.Safety.Confirmation);
        Assert.Empty(view.Password);

        if (rollBack)
        {
            File.Delete(setup.Archive);
            await view.ConfirmRollbackCommand.ExecuteAsync(null);
            Assert.Equal("Rollback complete.", view.RestoreStatus);
            Assert.Contains("Original state recovered", view.RestoreReport);
            Assert.Equal(setup.OriginalFirst, File.ReadAllBytes(setup.Targets[0].BookmarksPath));
            Assert.Equal(setup.OriginalSecond, File.ReadAllBytes(setup.Targets[1].BookmarksPath));
        }
        else
        {
            view.DeclineRollbackCommand.Execute(null);
            Assert.True(view.IsRestorePaused);
            Assert.True(view.ResumeRestoreCommand.CanExecute(null));
            Assert.Equal(setup.OriginalSecond, File.ReadAllBytes(setup.Targets[1].BookmarksPath));

            await view.ResumeRestoreCommand.ExecuteAsync(null);

            Assert.Equal("Bookmarks restored.", view.RestoreStatus);
            Assert.Contains("Restored", view.RestoreReport);
            Assert.Equal(setup.Native, File.ReadAllBytes(setup.Targets[1].BookmarksPath));
        }

        Assert.False(view.IsRestoreActive);
        Assert.Equal(safety, File.ReadAllBytes(review.SafetyPath));
        Assert.True(File.Exists(review.JournalPath));
    }

    [Fact]
    public async Task ChangedArchiveAfterReviewCreatesNoArtifacts()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await PrepareReview();
        using var shell = setup.Shell;
        var view = shell.RestorePreview;
        view.Planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
        await File.AppendAllTextAsync(setup.Archive, "changed");
        await view.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Contains("could not", view.RestoreStatus);
        Assert.Equal("RESTORE_CONTEXT_OR_IO_FAILED", view.RestoreErrorCode);
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        Assert.Equal(setup.OriginalFirst, File.ReadAllBytes(setup.Targets[0].BookmarksPath));
        Assert.Equal(setup.OriginalSecond, File.ReadAllBytes(setup.Targets[1].BookmarksPath));
    }

    private async Task<(ShellViewModel Shell, string Archive, string Recovery, BraveProfileInfo[] Targets,
        byte[] Native, byte[] OriginalFirst, byte[] OriginalSecond)> PrepareReview()
    {
        var sourceData = Path.Combine(_root, "source-data");
        var targetData = Path.Combine(_root, "destination-data");
        var native = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"));
        var originalFirst = native.Concat(new byte[] { (byte)' ' }).ToArray();
        var originalSecond = native.Concat(new byte[] { (byte)' ', (byte)' ' }).ToArray();
        BraveProfileInfo Create(string root, string name, byte[] content)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var bookmarks = Path.Combine(directory, "Bookmarks");
            File.WriteAllBytes(bookmarks, content);
            return new BraveProfileInfo(name, directory, bookmarks);
        }

        var source = new BraveAppInfo(BraveAppChannel.Stable, Version, "synthetic-source", sourceData);
        var target = new BraveAppInfo(BraveAppChannel.Stable, Version, "synthetic-target", targetData);
        BraveProfileInfo[] destinations = [Create(targetData, "Default", originalFirst), Create(targetData, "Profile 1", originalSecond)];
        var archive = Path.Combine(_root, "source.bravebackup");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        await BraveArchiveBackup.CreateAsync([new(source, [Create(sourceData, "Default", native), Create(sourceData, "Profile 1", native)])],
            ["bookmarks"], archive, ArchivePassword, "balanced", [sourceData], "synthetic", _ => Task.FromResult(false));

        var shell = new ShellViewModel(() => [target], _ => destinations, _ => new BraveProfileSummary(0, 0));
        try
        {
            await shell.RefreshAsync();
            shell.Activate(Assert.Single(shell.Installations));
            shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
            var view = shell.RestorePreview;
            view.SourcePath = archive;
            await view.EnterAsync();
            Assert.True(view.NeedsUnlock);
            view.Password = ArchivePassword;
            await view.UnlockAsync();
            Assert.False(view.NeedsUnlock);
            var profiles = Assert.Single(view.Planning.Groups).Profiles;
            Assert.Equal(2, profiles.Length);
            for (var index = 0; index < profiles.Length; index++)
            {
                var profile = profiles[index];
                profile.IsSelected = true;
                profile.SelectedInstallation = Assert.Single(profile.TargetInstallations);
                profile.SelectedProfile = profile.TargetProfiles.Single(item => item.Info == destinations[index]);
                Assert.Single(profile.Categories).IsSelected = true;
            }
            view.Safety.Folder = recovery;
            view.Safety.Password = view.Safety.Confirmation = "synthetic-safety-password";
            view.ReviewRestoreCommand.Execute(null);
            Assert.True(view.ConfirmRestoreCommand.CanExecute(null));
            return (shell, archive, recovery, destinations, native, originalFirst, originalSecond);
        }
        catch { shell.Dispose(); throw; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
