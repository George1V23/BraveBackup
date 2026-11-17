using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvExportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CsvVm_" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Profile => Path.Combine(Data, "Default");
    private string Destination => Path.Combine(_root, "output", "passwords.csv");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private BraveAppInfo App(string version = "1.102.13") => new(BraveAppChannel.Stable, version, "synthetic", Data);
    private BraveProfileInfo ProfileInfo => new("Synthetic", Profile, Path.Combine(Profile, "Bookmarks"));
    private int _unlocks;
    private PasswordCsvExportViewModel Create(bool prepare = true, string version = "1.102.13")
    {
        Directory.CreateDirectory(Profile); Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        var vm = new PasswordCsvExportViewModel { ObserveBrowserState = () => ProcessPresence.Stopped };
        var source = Assert.Single(vm.PasswordSources);
        var os = source.Provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "windows" : "linux";
        if (prepare)
        {
            var path = Path.Combine(Profile, "Login Data");
            File.Copy(Path.Combine(Fixtures, "Passwords", $"LoginData.v43.{os}.sqlite"), path);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery(); }
            File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(Profile, BraveAffiliationSnapshot.DatabaseFileName));
            if (os == "windows") File.WriteAllText(Path.Combine(Data, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
        }
        vm.Unlock = (_, _) =>
        {
            _unlocks++; using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
            return new PasswordKeyMaterial(source.Provider, Convert.FromHexString(vectors.RootElement.GetProperty(os + "KeyHex").GetString()!));
        };
        vm.SetContext(App(version), ProfileInfo); vm.Destination = Destination;
        return vm;
    }

    [Fact]
    public async Task MetadataSelectionDoesNotReadCredentialsAndNeedsExplicitSourceAndDialog()
    {
        using var vm = Create(prepare: false);
        Assert.Null(vm.SelectedPasswordSource); Assert.False(vm.ExportCsvCommand.CanExecute(null));
        vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        Assert.False(vm.ExportCsvCommand.CanExecute(null)); Assert.Contains("confirmation dialog", vm.Availability);
        Assert.Equal(0, _unlocks);
        await vm.ExportCsvAsync(); Assert.Equal(0, _unlocks); Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DedicatedCsvActionWarnsEachTimeAndReturnsOnlySafeCounts()
    {
        using var vm = Create(); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources); var confirmations = 0;
        vm.ConfirmPlaintextSave = (warning, _) => { confirmations++; Assert.Equal(Destination, warning.Destination); return Task.FromResult(true); };
        Assert.True(vm.ExportCsvCommand.CanExecute(null));
        await vm.ExportCsvAsync(); Assert.True(vm.Succeeded); Assert.Equal(Destination, vm.CompletedDestination);
        Assert.Contains("CSV rows: 1", vm.Report); Assert.DoesNotContain("fixture-user", vm.Report); Assert.DoesNotContain("example.invalid", vm.Report);
        await vm.ExportCsvAsync(); Assert.True(vm.Succeeded); Assert.Equal(2, confirmations); Assert.Equal(2, _unlocks);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public void DestinationsStaySeparatePerProfileAndInstallationChangesClearSource()
    {
        using var vm = Create(prepare: false); var source = Assert.Single(vm.PasswordSources); vm.SelectedPasswordSource = source;
        var other = new BraveProfileInfo("Other", Path.Combine(Data, "Profile 1"), "");
        vm.SetContext(App(), other); Assert.Empty(vm.Destination); Assert.Same(source, vm.SelectedPasswordSource);
        vm.Destination = Path.Combine(_root, "output", "other.csv"); vm.SetContext(App(), ProfileInfo);
        Assert.Equal(Destination, vm.Destination);
        vm.SetContext(App() with { UserDataDirectory = Path.Combine(_root, "different") }, null);
        Assert.Null(vm.SelectedPasswordSource); Assert.Equal(0, _unlocks);
    }

    [Fact]
    public async Task FreezeDestinationAndProviderWhileRejectingContextChanges()
    {
        using var vm = Create(); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        var different = Path.Combine(_root, "output", "changed.csv");
        vm.ConfirmPlaintextSave = (_, _) =>
        {
            Assert.True(vm.IsBusy); Assert.False(vm.ExportCsvCommand.CanExecute(null));
            Assert.Throws<InvalidOperationException>(() => vm.SetContext(App(), null));
            vm.Destination = different; vm.SelectedPasswordSource = null; return Task.FromResult(true);
        };
        await vm.ExportCsvAsync(); Assert.True(vm.Succeeded); Assert.Equal(Destination, vm.CompletedDestination);
        Assert.True(File.Exists(Destination)); Assert.False(File.Exists(different));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrDisposeDuringConfirmationPreservesDestination(bool dispose)
    {
        using var vm = Create(); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        File.WriteAllText(Destination, "synthetic-original");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ConfirmPlaintextSave = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; };
        var operation = vm.ExportCsvAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (dispose) vm.Dispose(); else vm.Cancel();
        await operation; Assert.False(vm.IsBusy); Assert.False(vm.Succeeded);
        Assert.Equal("synthetic-original", File.ReadAllText(Destination)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task UnexpectedHostFailureNeverCopiesExceptionContentsToStatus()
    {
        using var vm = Create(); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        vm.ConfirmPlaintextSave = (_, _) => throw new InvalidOperationException("synthetic-private-value");
        await vm.ExportCsvAsync(); Assert.False(vm.Succeeded); Assert.Contains("failed", vm.Status);
        Assert.DoesNotContain("synthetic-private-value", vm.Status); Assert.Empty(vm.Report); Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task UnsupportedVersionIsUnavailableBeforeKeyAccess()
    {
        using var vm = Create(prepare: false, version: "unknown"); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        vm.ConfirmPlaintextSave = (_, _) => throw new Exception("Must not prompt");
        Assert.Contains("1.102.13", vm.Availability); Assert.False(vm.ExportCsvCommand.CanExecute(null));
        await vm.ExportCsvAsync(); Assert.Equal(0, _unlocks);
    }

    [Fact]
    public async Task DestinationPickerOnlyChangesMetadataAndCancellationKeepsPath()
    {
        using var vm = Create(prepare: false); var original = vm.Destination;
        vm.PickDestination = _ => Task.FromResult<string?>(null);
        await vm.BrowseDestinationAsync(); Assert.Equal(original, vm.Destination);
        var picked = Path.Combine(_root, "output", "picked.csv");
        vm.PickDestination = _ => Task.FromResult<string?>(picked);
        await vm.BrowseDestinationAsync(); Assert.Equal(picked, vm.FullDestination); Assert.Equal(0, _unlocks); Assert.False(File.Exists(picked));
    }

    [Fact]
    public async Task HostInterlockPreventsStartingCsvWithoutAccessingCredentials()
    {
        using var vm = Create(prepare: false); vm.SelectedPasswordSource = Assert.Single(vm.PasswordSources);
        vm.ConfirmPlaintextSave = (_, _) => throw new Exception("Must not prompt");
        vm.IsHostAvailable = false; Assert.False(vm.ExportCsvCommand.CanExecute(null));
        Assert.Contains("Another operation", vm.Availability); await vm.ExportCsvAsync(); Assert.Equal(0, _unlocks);
    }

    [Fact]
    public async Task PickerResultStaysWithTheProfileThatOpenedIt()
    {
        using var vm = Create(prepare: false);
        var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PickDestination = _ => choice.Task;
        var browsing = vm.BrowseDestinationAsync();
        vm.SetContext(App(), new BraveProfileInfo("Other", Path.Combine(Data, "Profile 1"), ""));
        var picked = Path.Combine(_root, "output", "original-profile.csv"); choice.SetResult(picked);
        await browsing; Assert.Empty(vm.Destination);
        vm.SetContext(App(), ProfileInfo); Assert.Equal(picked, vm.Destination); Assert.Equal(0, _unlocks);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
