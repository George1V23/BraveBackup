using System.Security.Cryptography;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RestoreReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestoreReview_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReviewShowsExactPathsAndIsInvalidatedByChangingScopeOrSafetyInputs()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Path.Combine(_root, "browser");
        var profile = Directory.CreateDirectory(Path.Combine(data, "Default")).FullName;
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        var bookmarks = Path.Combine(profile, "Bookmarks");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), bookmarks);
        var original = File.ReadAllBytes(bookmarks);
        var archive = Path.Combine(_root, "input.bravebackup");
        var app = new BraveAppInfo(BraveAppChannel.Stable, "1.102.13", "synthetic", data);
        var target = new BraveProfileInfo("Destination", profile, bookmarks);
        await BraveArchiveBackup.CreateAsync([new(app, [target])], ["bookmarks"], archive,
            null, "balanced", [data], "synthetic", _ => Task.FromResult(false));

        using var view = new RestorePreviewViewModel();
        view.Planning.SetTargets([new(app, [new(target)])]);
        view.PickSource = () => Task.FromResult<string?>(archive);
        await view.BrowseAsync();
        // The source property starts validation immediately and EnterAsync awaits a fresh pass.
        await view.EnterAsync();
        var row = Assert.Single(Assert.Single(view.Planning.Groups).Profiles);
        row.IsSelected = true;
        row.SelectedInstallation = Assert.Single(row.TargetInstallations);
        row.SelectedProfile = Assert.Single(row.TargetProfiles);
        view.Safety.Folder = recovery;
        view.Safety.Password = "synthetic-safety";
        view.Safety.Confirmation = "wrong";
        var category = Assert.Single(row.Categories);
        Assert.True(category.IsSupported);
        Assert.False(category.IsSelected);
        category.IsSelected = true;
        Assert.False(view.ReviewRestoreCommand.CanExecute(null));
        view.Safety.Confirmation = "synthetic-safety";
        Assert.True(view.ReviewRestoreCommand.CanExecute(null));

        view.ReviewRestoreCommand.Execute(null);
        var review = Assert.IsType<RestoreReview>(view.Review);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), review.SourceSha256);
        Assert.Equal(bookmarks, Assert.Single(review.Targets).Profile.BookmarksPath);
        Assert.Contains(archive, view.ReviewSummary);
        Assert.Contains(bookmarks, view.ReviewSummary);
        Assert.Contains(review.SafetyPath, view.ReviewSummary);
        Assert.Contains(review.JournalPath, view.ReviewSummary);
        Assert.DoesNotContain(view.Safety.Password, view.ReviewSummary);
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(original, File.ReadAllBytes(bookmarks));

        view.CancelReviewCommand.Execute(null);
        Assert.False(view.HasReview);
        view.ReviewRestoreCommand.Execute(null);
        row.SelectedProfile = null;
        Assert.False(view.HasReview);
        row.SelectedProfile = Assert.Single(row.TargetProfiles);
        view.ReviewRestoreCommand.Execute(null);
        view.Safety.Password = "changed";
        Assert.False(view.HasReview);
        Assert.Empty(Directory.GetFiles(recovery));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
