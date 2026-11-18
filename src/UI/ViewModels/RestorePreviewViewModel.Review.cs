using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>One selected category with independently captured destination metadata.</summary>
public sealed record RestoreReviewedTarget(Guid SourceProfileId, BraveAppInfo Installation, BraveProfileInfo Profile, string Category)
{
    /// <summary>Gets the displayed name of the reviewed category.</summary>
    public string CategoryName => Category == "bookmarks" ? "Bookmarks" : "Settings";
}

/// <summary>A write-free review of the exact archive, destinations and encrypted artifact paths.</summary>
public sealed record RestoreReview(string ArchivePath, Guid ArchiveId, string SourceSha256, Guid OperationId,
    string SafetyPath, string JournalPath, IReadOnlyList<RestoreReviewedTarget> Targets, string DestinationSummary);

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

    private bool CanReviewRestore() => !_disposed && !IsRestoreActive && !IsBusy && Planning.ReadyForReview && Safety.ValidationError is null;

    private void OnSafetySettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RestoreSafetyOptionsViewModel.Folder)
            or nameof(RestoreSafetyOptionsViewModel.Password) or nameof(RestoreSafetyOptionsViewModel.Confirmation))
            InvalidateReview();
    }

    partial void OnReviewChanged(RestoreReview? value) => ConfirmRestoreCommand.NotifyCanExecuteChanged();

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
            var targets = profiles.SelectMany(row => row.Profile.Categories
                .Where(category => category.IsSelected && category.IsSupported)
                .Select(category => new RestoreReviewedTarget(row.Profile.SourceId,
                    row.Profile.SelectedInstallation!.Info with
                    {
                        UserDataDirectory = Path.GetFullPath(row.Profile.SelectedInstallation.Info.UserDataDirectory)
                    },
                    row.Profile.SelectedProfile!.Info with { ProfileDirectory = row.Profile.DestinationPath },
                    category.Id))).ToArray();

            var targetsBySource = targets.ToLookup(target => target.SourceProfileId);
            var summary = profiles.Select(row =>
            {
                var files = targetsBySource[row.Profile.SourceId].Select(target =>
                {
                    var path = Path.Combine(target.Profile.ProfileDirectory, BraveProfileRestorePlan.TargetFileName(target.Category));
                    return target.Category == "bookmarks" ? $"Replace Bookmarks at {path}" : $"Apply stored Settings to {path}";
                });
                return $"{row.Name} / {row.Profile.Name} → {row.Profile.SelectedInstallation!.Name} / {row.Profile.SelectedProfile!.Name}\n" +
                       string.Join("\n", files);
            });
            var description = string.Join("\n\n", summary);
            if (targets.Any(target => target.Category == "settings"))
                description += "\n\nSettings applies the choices stored in the archive. A choice absent from the archive keeps its destination value.";
            Review = new(source.Path, source.Id, source.Hash, operationId, artifacts.SafetyPath, artifacts.JournalPath,
                Array.AsReadOnly(targets), description);
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
