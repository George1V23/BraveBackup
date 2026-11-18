using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>One reviewed bookmark mapping, with independently captured destination metadata.</summary>
public sealed record RestoreReviewedBookmark(Guid SourceProfileId, BraveAppInfo Installation, BraveProfileInfo Profile);

/// <summary>A write-free review of the exact archive, destinations and encrypted artifact paths.</summary>
public sealed record RestoreReview(string ArchivePath, Guid ArchiveId, string SourceSha256, Guid OperationId,
    string SafetyPath, string JournalPath, IReadOnlyList<RestoreReviewedBookmark> Bookmarks, string DestinationSummary);

public partial class RestorePreviewViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReview), nameof(ReviewSummary))]
    private RestoreReview? _review;

    [ObservableProperty] private string _reviewError = string.Empty;
    public bool HasReview => Review is not null;
    public string ReviewSummary => Review is { } review
        ? $"Source backup\n{review.ArchivePath}\n\n{review.DestinationSummary}\n\n" +
          $"Encrypted safety backup\n{review.SafetyPath}\n\nRecovery journal\n{review.JournalPath}"
        : string.Empty;

    private bool CanReviewRestore() => !_disposed && !IsBusy && Planning.ReadyForReview && Safety.ValidationError is null;

    private void OnSafetySettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RestoreSafetyOptionsViewModel.Folder)
            or nameof(RestoreSafetyOptionsViewModel.Password) or nameof(RestoreSafetyOptionsViewModel.Confirmation))
            InvalidateReview();
    }

    private void InvalidateReview()
    {
        Review = null;
        ReviewError = string.Empty;
        ReviewRestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Freezes the reviewed source identity and the exact paths for this proposed operation.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewRestore))]
    private void ReviewRestore()
    {
        if (!CanReviewRestore() || Planning.ReviewedSource is not { } source) return;
        InvalidateReview();
        try
        {
            var operationId = Guid.NewGuid();
            var artifacts = new RestoreArtifactScope(operationId, Safety.FullFolder, source.Path, Planning.KnownBrowserRoots);
            var profiles = Planning.Groups.SelectMany(group => group.Profiles.Where(profile => profile.IsSelected)
                .Select(profile => (group.Name, Profile: profile))).ToArray();
            var bookmarks = profiles.Select(row => new RestoreReviewedBookmark(row.Profile.SourceId,
                row.Profile.SelectedInstallation!.Info with
                {
                    UserDataDirectory = Path.GetFullPath(row.Profile.SelectedInstallation.Info.UserDataDirectory)
                },
                row.Profile.SelectedProfile!.Info with { ProfileDirectory = row.Profile.DestinationPath })).ToArray();
            var summary = profiles.Select(row =>
                $"{row.Name} / {row.Profile.Name} → {row.Profile.SelectedInstallation!.Name} / {row.Profile.SelectedProfile!.Name}\n" +
                $"Replace Bookmarks at {Path.Combine(row.Profile.DestinationPath, "Bookmarks")}");
            Review = new(source.Path, source.Id, source.Hash, operationId, artifacts.SafetyPath, artifacts.JournalPath,
                Array.AsReadOnly(bookmarks), string.Join("\n\n", summary));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ReviewError = "Choose an accessible safety-backup folder outside all detected Brave data.";
        }
    }

    /// <summary>Discards prepared review details without changing the form or creating files.</summary>
    [RelayCommand]
    private void CancelReview() => InvalidateReview();
}
