using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Drives the reviewed restore flow end to end on synthetic archives and profiles; engine and files are real, Brave is not.</summary>
public sealed class RestoreExecutionViewModelTests : IDisposable
{
    private const string SourceJson = """{"roots":{},"version":1,"synthetic":"source-#"}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestoreExecutionVm_" + Guid.NewGuid().ToString("N"));
    private readonly string _archive, _safetyFolder;
    private ProcessPresence _presence = ProcessPresence.Stopped;
    private RestorePlanningViewModel _planning = null!;
    private RestoreSafetyOptionsViewModel _safety = null!;
    private RestoreExecutionViewModel _execution = null!;
    private string[] _destinationFiles = [];
    private int _clearedArchivePassword;

    public RestoreExecutionViewModelTests()
    {
        _archive = Path.Combine(_root, "out", "backup.bravebackup"); _safetyFolder = Path.Combine(_root, "safety"); Directory.CreateDirectory(_safetyFolder);
    }
    private (BraveAppInfo App, BraveProfileInfo[] Profiles) Browser(string name, string? bookmarks)
    {
        var data = Path.Combine(_root, name);
        var profiles = new[] { "Default", "Profile 1" }.Select(directory =>
        {
            var path = Path.Combine(data, directory); Directory.CreateDirectory(path);
            if (bookmarks is not null) File.WriteAllText(Path.Combine(path, "Bookmarks"), bookmarks.Replace("#", directory));
            File.WriteAllText(Path.Combine(path, "Preferences"), "{}");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(Path.Combine(path, "Preferences"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (bookmarks is not null) File.SetUnixFileMode(Path.Combine(path, "Bookmarks"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return new BraveProfileInfo(directory, path, Path.Combine(path, "Bookmarks"));
        }).ToArray();
        return (new(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data), profiles);
    }
    /// <summary>Builds the archive, planning/safety/execution models and a fully mapped, rechecked, review-ready plan.</summary>
    private async Task Setup(string? password = null, bool recheck = true)
    {
        var source = Browser("source", SourceJson);
        Directory.CreateDirectory(Path.GetDirectoryName(_archive)!);
        await BraveArchiveBackup.CreateAsync([new BraveBackupSelection(source.App, source.Profiles)], ["bookmarks"], _archive, password, "fast",
            [source.App.UserDataDirectory], "fixture", _ => Task.FromResult(false), null, default);
        var destination = Browser("destination", """{"roots":{},"version":1,"original":"#"}""");
        _destinationFiles = destination.Profiles.Select(p => p.BookmarksPath).ToArray();
        _planning = new RestorePlanningViewModel(); _safety = new RestoreSafetyOptionsViewModel();
        _planning.SetTargets([new RestoreTargetInstallation(destination.App, destination.Profiles.Select(p => new RestoreTargetProfile(p)).ToArray())]);
        var manifest = BraveArchiveInspection.Read(_archive, password).Manifest!;
        _planning.Load(manifest, Path.GetFullPath(_archive));
        var rows = _planning.Groups.Single().Profiles;
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].IsSelected = true; rows[index].SelectedInstallation = rows[index].TargetInstallations[0];
            rows[index].SelectedProfile = rows[index].TargetProfiles[index]; rows[index].SelectAllSupportedCommand.Execute(null);
        }
        _planning.ObserveBrowserState = _ => _presence;
        if (recheck) await _planning.RecheckAsync();
        _safety.Folder = _safetyFolder; _safety.Password = "synthetic-safety"; _safety.Confirmation = "synthetic-safety";
        _execution = new RestoreExecutionViewModel(_planning, _safety, () => password, () => _clearedArchivePassword++)
        {
            Post = action => action(),
            CreateSession = (archive, pw, items, folder, safety, token) => BraveBookmarksRestoreSession.Create(archive, pw, items, folder, safety, () => _presence, token)
        };
    }
    private string[] Destinations => _destinationFiles.Select(File.ReadAllText).ToArray();
    private void AssertOriginals() => Assert.Equal(new[] { Original("Default"), Original("Profile 1") }, Destinations);
    private static string Original(string profile) => "{\"roots\":{},\"version\":1,\"original\":\"" + profile + "\"}";

    [Fact]
    public async Task BookmarksBecomeSelectableAndReviewNeedsEveryPrerequisite()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup(recheck: false);
        var category = _planning.Groups.Single().Profiles[0].Categories.Single();
        Assert.True(category.IsSupported); Assert.True(category.IsSelected);
        Assert.False(_planning.IsReadyForReview); Assert.False(_execution.CanReviewNow);       // not rechecked yet
        await _planning.RecheckAsync(); Assert.True(_execution.CanReviewNow);
        _safety.Confirmation = "different"; Assert.False(_execution.CanReviewNow);
        _safety.Confirmation = "synthetic-safety";
        _presence = ProcessPresence.Running; await _planning.RecheckAsync(); Assert.False(_execution.CanReviewNow);
        _presence = ProcessPresence.Stopped; await _planning.RecheckAsync();
        category.IsSelected = false; Assert.False(_execution.CanReviewNow); category.IsSelected = true;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-archive-key")]
    public async Task ReviewWritesNothingConfirmRestoresChecklistAndRollbackRecovers(string? password)
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup(password);
        await _execution.ReviewCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Reviewing, _execution.Phase);
        Assert.Contains("REPLACES", _execution.ReviewText); Assert.Contains(Path.GetFullPath(_archive), _execution.ReviewText);
        AssertOriginals(); Assert.Empty(Directory.GetFiles(_safetyFolder));
        _execution.CancelReviewCommand.Execute(null);
        Assert.Equal(RestorePhase.Idle, _execution.Phase); Assert.Empty(Directory.GetFiles(_safetyFolder));
        await _execution.ReviewCommand.ExecuteAsync(null);
        await _execution.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Completed, _execution.Phase); Assert.Equal(100, _execution.Progress);
        Assert.All(_execution.Checklist, item => Assert.True(item.IsDone));
        Assert.All(Destinations, text => Assert.Contains("synthetic", text)); Assert.All(Destinations, text => Assert.DoesNotContain("original", text));
        Assert.True(_execution.CanOfferRollback); Assert.Equal(string.Empty, _safety.Password); Assert.True(_clearedArchivePassword > 0);
        Assert.DoesNotContain("synthetic-safety", _execution.Report); Assert.DoesNotContain("synthetic-archive-key", _execution.Report); Assert.DoesNotContain("example", _execution.Report);
        _execution.OfferRollbackCommand.Execute(null); Assert.True(_execution.RollbackPromptVisible);
        await _execution.RollBackCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.RolledBack, _execution.Phase); AssertOriginals();
        Assert.All(_execution.Checklist, item => Assert.True(item.IsDone));          // during/after rollback a tick means recovered
        _execution.DoneCommand.Execute(null); Assert.Equal(RestorePhase.Idle, _execution.Phase);
    }

    [Fact]
    public async Task PauseAsksAboutRollbackNotNowLeavesItPausedAndResumeFinishes()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup();
        var closeBlockedWhileRunning = false;
        _execution.Post = action =>
        {
            action();
            if (_execution.Phase == RestorePhase.Running && _execution.Checklist[0].IsDone)
            { closeBlockedWhileRunning = !_execution.TryClose(); _execution.CancelRestoreCommand.Execute(null); }
        };
        await _execution.ReviewCommand.ExecuteAsync(null); await _execution.ConfirmCommand.ExecuteAsync(null);
        Assert.True(closeBlockedWhileRunning); Assert.NotEmpty(_execution.CloseNotice);
        Assert.Equal(RestorePhase.Paused, _execution.Phase); Assert.True(_execution.RollbackPromptVisible);
        Assert.True(_execution.Checklist[0].IsDone); Assert.False(_execution.Checklist[1].IsDone);
        Assert.InRange(_execution.Progress, 1, 99); Assert.True(_execution.TryClose());       // paused work does not block closing
        Assert.Equal(Original("Profile 1"), File.ReadAllText(_destinationFiles[1]));
        _execution.NotNowCommand.Execute(null); Assert.Equal(RestorePhase.Paused, _execution.Phase); Assert.False(_execution.RollbackPromptVisible);
        _execution.Post = action => action();
        await _execution.ResumeCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Completed, _execution.Phase); Assert.All(_execution.Checklist, item => Assert.True(item.IsDone));
    }

    [Fact]
    public async Task PausedRestoreCanBeRolledBackAndResumeIsRefusedWhileBraveRuns()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup();
        _execution.Post = action => { action(); if (_execution.Phase == RestorePhase.Running && _execution.Checklist[0].IsDone) _execution.CancelRestoreCommand.Execute(null); };
        await _execution.ReviewCommand.ExecuteAsync(null); await _execution.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Paused, _execution.Phase);
        _presence = ProcessPresence.Running;
        await _execution.ResumeCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Paused, _execution.Phase); Assert.Contains("Brave", _execution.Message);
        Assert.Equal(Original("Profile 1"), File.ReadAllText(_destinationFiles[1]));
        _presence = ProcessPresence.Stopped;
        await _execution.RollBackCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.RolledBack, _execution.Phase); AssertOriginals();
    }

    [Fact]
    public async Task RunningBrowserAtConfirmationFailsWithoutAnyChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup();
        await _execution.ReviewCommand.ExecuteAsync(null);
        _presence = ProcessPresence.Running;
        await _execution.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Failed, _execution.Phase); Assert.Contains("Brave", _execution.Message);
        AssertOriginals(); Assert.False(_execution.CanOfferRollback);
        Assert.All(_execution.Checklist, item => Assert.False(item.IsDone));
        _execution.DoneCommand.Execute(null); Assert.Equal(RestorePhase.Idle, _execution.Phase);
    }

    [Fact]
    public async Task ReviewFailureReturnsToIdleWithSanitizedMessageAndNothingWritten()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup();
        File.Delete(_archive);
        await _execution.ReviewCommand.ExecuteAsync(null);
        Assert.Equal(RestorePhase.Idle, _execution.Phase); Assert.NotEmpty(_execution.Message); Assert.DoesNotContain(_root, _execution.Message);
        AssertOriginals(); Assert.Empty(Directory.GetFiles(_safetyFolder));
    }

    [Fact]
    public async Task ReportSavesToNewFileOnlyAndNeverOverwrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Setup();
        await _execution.ReviewCommand.ExecuteAsync(null); await _execution.ConfirmCommand.ExecuteAsync(null);
        var path = Path.Combine(_root, "report.txt"); _execution.PickReportFile = () => Task.FromResult<string?>(path);
        await _execution.SaveReportCommand.ExecuteAsync(null);
        Assert.Contains("Outcome: Completed", File.ReadAllText(path)); var before = File.ReadAllText(path);
        File.WriteAllText(path, "keep");
        await _execution.SaveReportCommand.ExecuteAsync(null);
        Assert.Equal("keep", File.ReadAllText(path)); Assert.Contains("could not be saved", _execution.Message);
        string? opened = null; _execution.OpenFolder = folder => opened = folder;
        _execution.OpenSafetyFolderCommand.Execute(null); Assert.Equal(Path.GetFullPath(_safetyFolder), opened);
        Assert.NotEmpty(before);
    }

    public void Dispose() { _execution?.Dispose(); _planning?.Dispose(); _safety?.Dispose(); Directory.Delete(_root, true); }
}
