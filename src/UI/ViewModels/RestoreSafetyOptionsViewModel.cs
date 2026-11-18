using System;
using System.IO;
using System.Threading.Tasks;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Reviewed encrypted safety-backup settings. Editing/browsing performs no filesystem writes.</summary>
public partial class RestoreSafetyOptionsViewModel : ViewModelBase, IDisposable
{
    private bool _disposed;
    private long _generation;
    public Func<Task<string?>>? PickFolder { get; set; }
    [ObservableProperty] private string _folder = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmation = string.Empty;
    public string FullFolder { get { try { return string.IsNullOrWhiteSpace(Folder) ? string.Empty : Path.GetFullPath(Folder); } catch { return Folder; } } }
    public string Validation => ValidationError ??
        "Safety settings entered. The encrypted backup is created after you confirm restoration.";

    internal string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Folder)) return "Choose an existing safety-backup folder outside browser data.";
            try
            {
                if (!Directory.Exists(FullFolder)) return "Choose an existing safety-backup folder.";
                ExportDestinationGuard.Validate(Path.Combine(FullFolder, "safety-destination-check"));
            }
            catch { return "The safety-backup folder is invalid, linked or inside recognized browser data."; }
            return Password.Length == 0 ? "Enter a separate safety-backup encryption password."
                : Password != Confirmation ? "Safety-backup passwords do not match."
                : null;
        }
    }
    partial void OnFolderChanged(string value) { _generation++; OnPropertyChanged(nameof(FullFolder)); OnPropertyChanged(nameof(Validation)); }
    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(Validation));
    partial void OnConfirmationChanged(string value) => OnPropertyChanged(nameof(Validation));
    /// <summary>Selects an existing folder; ignores picker results after source/lifetime changes.</summary>
    [RelayCommand]
    public async Task BrowseFolderAsync()
    {
        if (_disposed || PickFolder is null) return;
        var generation = _generation;
        try
        {
            var selected = await PickFolder();
            if (!_disposed && generation == _generation && !string.IsNullOrWhiteSpace(selected)) Folder = selected;
        }
        catch { /* Existing folder remains available for correction; no writes or secrets are logged. */ }
    }
    /// <summary>Clears secret text and invalidates pending folder selections; retains the nonsecret folder preference.</summary>
    public void ClearSecrets() { _generation++; Password = string.Empty; Confirmation = string.Empty; }
    public void Dispose() { if (_disposed) return; ClearSecrets(); _disposed = true; PickFolder = null; }
}
