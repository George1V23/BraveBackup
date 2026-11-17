using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ShellPasswordCsvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShellCsv_" + Guid.NewGuid().ToString("N"));
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private BraveAppInfo App(int index) => new(BraveAppChannel.Stable, "1.102.13", "synthetic", Path.Combine(_root, "data" + index));
    private BraveProfileInfo Profile(BraveAppInfo app, string directory = "Default") => new("Synthetic", Path.Combine(app.UserDataDirectory, directory), Path.Combine(app.UserDataDirectory, directory, "Bookmarks"));
    private int _detections;
    private int _unlocks;
    private async Task<ShellViewModel> Create()
    {
        foreach (var app in new[] { App(0), App(1) })
        {
            Directory.CreateDirectory(Profile(app).ProfileDirectory);
            File.Copy(Path.Combine(Fixtures, "Bookmarks.sample.json"), Profile(app).BookmarksPath);
        }
        Directory.CreateDirectory(Path.Combine(_root, "output"));
        var shell = new ShellViewModel(() => { _detections++; return new[] { App(0), App(1) }; }, app => new[] { Profile(app) }, _ => new BraveProfileSummary(1, 0));
        await shell.RefreshAsync(); shell.Activate(shell.Installations[0]);
        var csv = shell.PasswordCsvExport; var source = Assert.Single(csv.PasswordSources);
        var os = source.Provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "windows" : "linux";
        var database = Path.Combine(Profile(App(0)).ProfileDirectory, "Login Data");
        File.Copy(Path.Combine(Fixtures, "Passwords", $"LoginData.v43.{os}.sqlite"), database);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery(); }
        File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(Profile(App(0)).ProfileDirectory, BraveAffiliationSnapshot.DatabaseFileName));
        if (os == "windows") File.WriteAllText(Path.Combine(App(0).UserDataDirectory, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
        csv.Destination = Path.Combine(_root, "output", "passwords.csv"); csv.SelectedPasswordSource = source;
        csv.ObserveBrowserState = () => ProcessPresence.Stopped;
        csv.Unlock = (_, _) =>
        {
            _unlocks++; using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
            return new PasswordKeyMaterial(source.Provider, Convert.FromHexString(vectors.RootElement.GetProperty(os + "KeyHex").GetString()!));
        };
        return shell;
    }

    [Fact]
    public async Task ExistingExportProfileFeedsCsvAndRepeatedContextNotificationsPreserveResults()
    {
        using var shell = await Create();
        var csv = shell.PasswordCsvExport; csv.ConfirmPlaintextSave = (_, _) => Task.FromResult(true);
        Assert.Equal(0, _unlocks); Assert.True(csv.ExportCsvCommand.CanExecute(null));
        await csv.ExportCsvAsync(); Assert.True(csv.Succeeded); Assert.Equal(1, _unlocks);
        Assert.NotEmpty(csv.Report); Assert.True(File.Exists(csv.CompletedDestination));
        var completed = csv.CompletedDestination;
        shell.Activate(shell.ActiveInstallation!);
        Assert.Equal(completed, csv.CompletedDestination); Assert.True(csv.Succeeded);
        shell.Activate(shell.Installations[1]); Assert.Null(csv.SelectedPasswordSource); Assert.Empty(csv.Destination);
        shell.Activate(shell.Installations[0]); Assert.Equal(completed, csv.Destination);
    }

    [Fact]
    public async Task CsvBusyFreezesNavigationRefreshAndConflictingActionsWhileCancelRemainsUsable()
    {
        using var shell = await Create(); var csv = shell.PasswordCsvExport;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        csv.ConfirmPlaintextSave = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; };
        var active = shell.ActiveInstallation; var detections = _detections;
        var operation = csv.ExportCsvAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shell.IsWorkspaceEnabled); Assert.False(shell.CanUseBookmarkExport); Assert.True(shell.CanUsePasswordCsv);
        Assert.True(csv.CancelCommand.CanExecute(null));
        shell.Activate(shell.Installations[1]); Assert.Same(active, shell.ActiveInstallation);
        await shell.RefreshAsync(); Assert.Equal(detections, _detections);
        csv.Cancel(); await operation;
        Assert.True(shell.IsWorkspaceEnabled); Assert.True(shell.CanUseBookmarkExport); Assert.False(csv.Succeeded);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "output")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
