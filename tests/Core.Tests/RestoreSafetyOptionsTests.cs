using UI.ViewModels;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RestoreSafetyOptionsTests
{
    [Fact]
    public void IndependentPasswordsValidateAndClearWithoutWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "SafetyOptions_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var options = new RestoreSafetyOptionsViewModel(); options.Folder = root;
            Assert.Contains("separate", options.Validation); options.Password = "synthetic";
            Assert.Contains("do not match", options.Validation); options.Confirmation = options.Password;
            Assert.Contains("after you confirm", options.Validation); Assert.Empty(Directory.GetFiles(root));
            options.ClearSecrets(); Assert.Empty(options.Password); Assert.Empty(options.Confirmation); Assert.Equal(root, options.Folder);
            using var preview = new RestorePreviewViewModel(); preview.Safety.Password = "synthetic"; preview.Safety.Confirmation = "synthetic";
            preview.Leave(); Assert.Empty(preview.Safety.Password); Assert.Empty(preview.Safety.Confirmation);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LateFolderPickerResultIsIgnoredAfterClearingSecrets()
    {
        using var options = new RestoreSafetyOptionsViewModel(); var selected = new TaskCompletionSource<string?>();
        options.PickFolder = () => selected.Task; var operation = options.BrowseFolderAsync();
        options.ClearSecrets(); selected.SetResult(Path.GetTempPath()); await operation;
        Assert.Empty(options.Folder); options.Dispose(); Assert.Null(options.PickFolder);
    }
}
