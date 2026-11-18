using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BraveBackup.Core;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using UI;
using UI.ViewModels;
using UI.Views;

// This opt-in desktop test must never discover or unlock a real browser profile.
if (!OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("This smoke runner exercises the Linux provider UI.");
var repo = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(repo, "AGENTS.md")) ||
    !Directory.Exists(Path.Combine(repo, "tests", "Core.Tests", "Fixtures")))
    throw new ArgumentException("Run from the repository root or pass its absolute path.");
var root = Path.Combine(Path.GetTempPath(), "bravebackup-password-ui-preview-" + Guid.NewGuid().ToString("N"));
var profileDirectory = Path.Combine(root, "data", "Default");
Directory.CreateDirectory(profileDirectory); Directory.CreateDirectory(Path.Combine(root, "output"));
var fixtures = Path.Combine(repo, "tests", "Core.Tests", "Fixtures");
File.Copy(Path.Combine(fixtures, "Bookmarks.sample.json"), Path.Combine(profileDirectory, "Bookmarks"));
var database = Path.Combine(profileDirectory, "Login Data");
File.Copy(Path.Combine(fixtures, "Passwords", "LoginData.v43.linux.sqlite"), database);
using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
{ db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery(); }
File.Copy(Path.Combine(fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(profileDirectory, BraveAffiliationSnapshot.DatabaseFileName));
var app = new BraveAppInfo(BraveAppChannel.Stable, "1.102.13", "synthetic", Path.Combine(root, "data"));
var profile = new BraveProfileInfo("Synthetic profile", profileDirectory, Path.Combine(profileDirectory, "Bookmarks"));
var before = Directory.GetFiles(profileDirectory).ToDictionary(path => path, File.ReadAllBytes);
Trace.Listeners.Add(new ConsoleTraceListener());
return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
    .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
    .AfterSetup(_ => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Startup += (_, _) =>
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var window = (MainWindow)desktop.MainWindow!;
        var onOpened = typeof(MainWindow).GetMethod("OnOpened", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        window.Opened -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, onOpened);
        (window.DataContext as ShellViewModel)?.Dispose();
        var shell = new ShellViewModel(() => [app], _ => [profile], _ => new BraveProfileSummary(1, 0));
        window.DataContext = shell;
        window.Width = 1160; window.Height = 860;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await shell.RefreshAsync(); shell.Activate(shell.Installations[0]);
                shell.SelectedTabIndex = ShellViewModel.ExportTabIndex;
                var csv = shell.PasswordCsvExport;
                csv.Destination = Path.Combine(root, "output", "passwords.csv");
                csv.PickDestination = _ => Task.FromResult<string?>(csv.Destination);
                csv.ConfirmPlaintextSave = (warning, token) => (Task<bool>)typeof(MainWindow)
                    .GetMethod("ConfirmPasswordCsvSaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [warning, token])!;
                csv.ObserveBrowserState = () => ProcessPresence.Stopped;
                csv.Unlock = (_, _) =>
                {
                    using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "Passwords", "Crypto.synthetic.json")));
                    return new PasswordKeyMaterial(PasswordEncryptionProvider.LinuxKeyringV11,
                        Convert.FromHexString(vectors.RootElement.GetProperty("linuxKeyHex").GetString()!));
                };
                await Task.Delay(300);
                var section = window.GetVisualDescendants().OfType<PasswordCsvExportView>().Single();
                var source = section.GetVisualDescendants().OfType<ComboBox>().Single();
                var export = section.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Export password CSV"));
                Check(source.SelectedItem is null && !export.IsEffectivelyEnabled, "source starts unselected and export is disabled");
                source.SelectedIndex = 0; await Task.Delay(100);
                Check(csv.SelectedPasswordSource is not null && export.IsEffectivelyEnabled, "provider selector binding enables ready CSV");
                var outer = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .First(s => s.Content is StackPanel panel && panel.GetVisualDescendants().Contains(section));
                outer.Offset = new Vector(0, outer.Extent.Height); await Task.Delay(200);
                Check(section.Bounds.Height > 100 && section.Bounds.Width > 300, "CSV section has real laid-out bounds");
                var denied = csv.ExportCsvAsync();
                var dialog = await WaitDialog(desktop, "Save readable passwords?");
                Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(csv.Destination) == true), "real CSV warning contains full destination");
                Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("readable by anyone with file access") == true), "real CSV warning states plaintext risk");
                Click(dialog, "Cancel"); await denied;
                Check(!File.Exists(csv.Destination) && !csv.Succeeded, "CSV dialog Cancel publishes nothing");
                var accepted = csv.ExportCsvAsync(); dialog = await WaitDialog(desktop, "Save readable passwords?");
                Click(dialog, "Save plaintext CSV"); await accepted;
                Check(csv.Succeeded && File.Exists(csv.CompletedDestination), "CSV dialog approval saves synthetic CSV");
                Check(System.Text.RegularExpressions.Regex.IsMatch(csv.Report,
                    @"\ASource logins: [0-9]+; credential groups: [0-9]+; CSV rows: [0-9]+; excluded entries: [0-9]+; bytes: [0-9]+\z"),
                    "visible report contains counts only");
                await Task.Delay(150); outer.Offset = new Vector(0, outer.Extent.Height); await Task.Delay(200);
                var screenshot = Path.Combine(repo, "docs", "screenshots", "password-csv-section.png");
                using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                { bitmap.Render(window); bitmap.Save(screenshot, PngBitmapEncoderOptions.Default); }
                foreach (var file in before) Check(file.Value.AsSpan().SequenceEqual(File.ReadAllBytes(file.Key)), "synthetic source bytes unchanged");
                // Backup tab: steps 2 and below stay hidden until a profile is selected; only existing categories are offered.
                shell.SelectedTabIndex = ShellViewModel.BackupTabIndex; await Task.Delay(200);
                bool Visible(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
                Check(Visible("1   Select profiles"), "Backup step 1 is visible without a selection");
                Check(!Visible("2   Select data to include") && !Visible("3   Backup options") && !Visible("4   Backup summary"), "Backup steps 2-4 are hidden until a profile is selected");
                Check(!window.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Create Backup") && b.IsEffectivelyVisible), "Create Backup is hidden until a profile is selected");
                shell.Installations[0].Profiles[0].IsSelected = true; await Task.Delay(200);
                Check(Visible("2   Select data to include") && Visible("3   Backup options") && Visible("4   Backup summary"), "Backup steps 2-4 appear once a profile is selected");
                var titles = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.FontWeight == Avalonia.Media.FontWeight.SemiBold)
                    .Select(t => t.Text).ToHashSet();
                Check(titles.Contains("Bookmarks") && titles.Contains("Passwords") &&
                      !new[] { "Sessions", "Extensions", "Settings", "TabManagerPlus" }.Any(titles.Contains), "only categories existing in the selected profile are listed");
                shell.Installations[0].Profiles[0].IsSelected = false; await Task.Delay(200);
                Check(!Visible("2   Select data to include"), "deselecting the profile hides steps 2-4 again");
                // Exercise real archive-CSV dialogs without native key access.
                shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
                shell.Installations[0].Profiles[0].IsSelected = true;
                foreach (var category in shell.Options.Categories) category.IsSelected = category.Id == "passwords";
                shell.Options.SelectedPasswordSource = shell.Options.PasswordSources[0];
                shell.Options.ProtectPasswords = false; shell.Options.EncryptBackup = false;
                shell.Options.BackupPath = Path.Combine(root, "output", "csv-cancel.bravebackup");
                shell.PasswordBrowserState = () => ProcessPresence.Stopped;
                shell.PasswordKeyUnlocker = (_, state, token) => csv.Unlock!(state, token);
                shell.ConfirmPasswordCsvArchiveSave = (warning, token) => (Task<bool>)typeof(MainWindow)
                    .GetMethod("ConfirmPasswordCsvArchiveSaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [warning, token])!;
                var declinedArchive = shell.CreateBackupAsync();
                dialog = await WaitDialog(desktop, "Save password CSV inside backup?");
                Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(shell.Options.BackupPath) == true), "archive CSV dialog shows full destination");
                Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("readable by anyone with file access") == true), "archive CSV dialog states plaintext risk");
                Click(dialog, "Cancel backup"); await declinedArchive;
                Check(!shell.BackupSucceeded && !File.Exists(shell.Options.BackupPath), "archive CSV dialog Cancel publishes nothing");
                var closedArchive = shell.CreateBackupAsync(); dialog = await WaitDialog(desktop, "Save password CSV inside backup?");
                dialog.Close(); await closedArchive;
                Check(!shell.BackupSucceeded && !File.Exists(shell.Options.BackupPath), "closing archive CSV dialog declines saving");
                foreach (var encryptedCsv in new[] { false, true })
                {
                    shell.Options.EncryptBackup = encryptedCsv;
                    shell.Options.Password = "Synthetic-archive-key-4827"; shell.Options.PasswordConfirmation = shell.Options.Password;
                    shell.Options.BackupPath = Path.Combine(root, "output", encryptedCsv ? "csv-encrypted.bravebackup" : "csv-plain.bravebackup");
                    var saveCsvArchive = shell.CreateBackupAsync(); dialog = await WaitDialog(desktop, "Save password CSV inside backup?");
                    Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(encryptedCsv ? "after decrypting" : "unencrypted archive") == true), "archive CSV dialog explains actual encryption state");
                    if (!encryptedCsv)
                    {
                        using var warningBitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96));
                        warningBitmap.Render(dialog);
                        warningBitmap.Save(Path.Combine(repo, "docs", "screenshots", "password-csv-archive-confirmation.png"), PngBitmapEncoderOptions.Default);
                    }
                    Click(dialog, "Save backup with CSV"); await saveCsvArchive;
                    Check(shell.BackupSucceeded, "real archive CSV approval publishes backup");
                    using var csvInput = File.OpenRead(shell.Options.BackupPath);
                    Check(BackupArchive.Validate(csvInput, encryptedCsv ? "Synthetic-archive-key-4827" : null).FormatVersion == 8, "CSV archive validates as v8");
                }
                shell.Options.BackupPath = Path.Combine(root, "output", "csv-operation-cancel.bravebackup");
                shell.Options.EncryptBackup = false;
                var cancelledArchive = shell.CreateBackupAsync(); dialog = await WaitDialog(desktop, "Save password CSV inside backup?");
                shell.CancelBackup(); await cancelledArchive;
                Check(!dialog.IsVisible && !File.Exists(shell.Options.BackupPath), "operation cancellation closes CSV archive dialog without saving");
                foreach (var file in before) Check(file.Value.AsSpan().SequenceEqual(File.ReadAllBytes(file.Key)), "CSV archive source bytes unchanged");
                shell.Options.ProtectPasswords = true; shell.Options.EncryptBackup = true;
                // Read-only Restore phase 1 against the synthetic archive created above.
                shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
                var restore = shell.RestorePreview;
                restore.SourcePath = Path.Combine(root, "output", "csv-encrypted.bravebackup");
                var previewDeadline = DateTime.UtcNow.AddSeconds(15);
                while (restore.IsBusy && DateTime.UtcNow < previewDeadline) await Task.Delay(25);
                Check(!restore.IsBusy && restore.NeedsUnlock && restore.Installations.Count == 0, "Restore waits for explicit archive unlock");
                var restoreView = window.GetVisualDescendants().OfType<RestorePreviewView>().Single();
                Check(restoreView.GetVisualDescendants().OfType<TextBox>().Any(box => box.PasswordChar == '●'), "Restore password field is masked");
                restore.Password = "Synthetic-archive-key-4827"; await restore.UnlockAsync(); await Task.Delay(200);
                Check(restore.Installations.Count == 1 && restore.Summary.Contains("format: 8"), "Restore renders validated archive inventory");
                Check(restoreView.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("CSV passwords") == true), "Restore displays password representation without credential values");
                using (var restoreBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                {
                    restoreBitmap.Render(window);
                    restoreBitmap.Save(Path.Combine(repo, "docs", "screenshots", "restore-phase1-preview.png"), PngBitmapEncoderOptions.Default);
                }
                var planning = restore.Planning;
                var planningView = window.GetVisualDescendants().OfType<RestorePlanningView>().Single();
                var sourceChoice = planning.Groups.Single().Profiles.Single();
                Check(!sourceChoice.IsSelected, "Restore source choices start unselected");
                var targetBoxes = planningView.GetVisualDescendants().OfType<ComboBox>().ToArray();
                Check(targetBoxes.Length == 2 && targetBoxes.All(box => box.SelectedItem is null), "Restore destination selectors start empty");
                var sourceCheck = planningView.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, sourceChoice.Name));
                sourceCheck.IsChecked = true; await Task.Delay(100);
                Check(sourceChoice.IsSelected, "Restore source checkbox binding selects only archived source");
                Check(planningView.GetVisualDescendants().OfType<CheckBox>().Where(box => !ReferenceEquals(box, sourceCheck)).All(box => !box.IsEffectivelyEnabled), "unsupported restore categories remain disabled");
                targetBoxes[0].SelectedIndex = 0; await Task.Delay(100); targetBoxes[1].SelectedIndex = 0; await Task.Delay(100);
                Check(sourceChoice.SelectedProfile is not null && sourceChoice.DestinationPath == profileDirectory, "Restore mapping bindings show exact destination path");
                planning.ObserveBrowserState = _ => ProcessPresence.Stopped;
                await planning.RecheckAsync();
                Check(sourceChoice.CheckResult.StartsWith("Closed"), "Restore Recheck reports controlled closed-browser status");
                Check(planning.Availability.Contains("restoration is unavailable"), "Restore planning cannot execute destination writes");
                var restoreScroll = restoreView.GetVisualDescendants().OfType<ScrollViewer>().First();
                restoreScroll.Offset = new Vector(0, restoreScroll.Extent.Height); await Task.Delay(200);
                using (var planningBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                {
                    planningBitmap.Render(window);
                    planningBitmap.Save(Path.Combine(repo, "docs", "screenshots", "restore-phase2-mapping.png"), PngBitmapEncoderOptions.Default);
                }
                var safetyView = window.GetVisualDescendants().OfType<RestoreSafetyOptionsView>().Single();
                Check(safetyView.GetVisualDescendants().OfType<TextBox>().Count(box => box.PasswordChar == '●') == 2,
                    "safety encryption and confirmation fields are masked");
                restore.Safety.Folder = Path.Combine(root, "output");
                restore.Safety.Password = "Synthetic-safety-password";
                restore.Safety.Confirmation = "different-synthetic-password";
                await Task.Delay(100);
                Check(safetyView.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("do not match") == true),
                    "safety password mismatch is visible");
                restore.Safety.Confirmation = restore.Safety.Password;
                await Task.Delay(100);
                Check(restore.Safety.IsValid, "valid safety settings alone do not start a restore");
                restoreScroll.Offset = new Vector(0, restoreScroll.Extent.Height); await Task.Delay(200);
                using (var safetyBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                {
                    safetyBitmap.Render(window);
                    safetyBitmap.Save(Path.Combine(repo, "docs", "screenshots", "restore-safety-settings.png"), PngBitmapEncoderOptions.Default);
                }
                restore.Password = "synthetic-not-retained";
                shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
                Check(restore.Safety.Password.Length == 0 && restore.Safety.Confirmation.Length == 0,
                    "leaving Restore clears independent safety secrets");
                Check(restore.Password.Length == 0 && restore.Installations.Count == 0, "leaving Restore clears password and preview");
                foreach (var file in before) Check(file.Value.AsSpan().SequenceEqual(File.ReadAllBytes(file.Key)), "Restore inspection leaves source profile bytes unchanged");
                // Exercise raw fallback with a deliberately corrupt synthetic database and actual UI dispatcher.
                File.WriteAllText(database, "synthetic corrupt password database");
                shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
                shell.Installations[0].Profiles[0].IsSelected = true;
                foreach (var category in shell.Options.Categories) category.IsSelected = category.Id == "passwords";
                shell.Options.SelectedPasswordSource = shell.Options.PasswordSources[0];
                shell.Options.Password = "Synthetic-archive-key-4827"; shell.Options.PasswordConfirmation = shell.Options.Password;
                shell.Options.BackupPath = Path.Combine(root, "output", "raw.bravebackup");
                shell.PasswordBrowserState = () => ProcessPresence.Stopped;
                shell.ConfirmRawPasswordPreservation = (warning, token) => (Task<bool>)typeof(MainWindow)
                    .GetMethod("ConfirmRawPasswordPreservationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [warning, token])!;
                var raw = shell.CreateBackupAsync(); dialog = await WaitDialog(desktop, "Password capture could not complete");
                Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("corrupt") == true), "raw warning explains capture issue");
                Click(dialog, "Preserve raw files"); await raw;
                Check(shell.BackupSucceeded, "real raw-preservation approval creates encrypted archive");
                using (var input = File.OpenRead(shell.Options.BackupPath)) Check(BackupArchive.Validate(input, "Synthetic-archive-key-4827").FormatVersion == 7, "raw archive validates as v7");
                // Bookmarks-only restore through the real Restore page: review, confirm, checklist, rollback.
                var sampleBytes = File.ReadAllBytes(Path.Combine(profileDirectory, "Bookmarks"));
                shell.SelectedTabIndex = ShellViewModel.BackupTabIndex; shell.Installations[0].Profiles[0].IsSelected = true;
                foreach (var category in shell.Options.Categories) category.IsSelected = category.Id == "bookmarks";
                shell.Options.EncryptBackup = false; shell.Options.BackupPath = Path.Combine(root, "output", "bookmarks.bravebackup");
                await shell.CreateBackupAsync(); Check(shell.BackupSucceeded, "bookmarks-only archive created for the restore smoke");
                File.WriteAllText(Path.Combine(profileDirectory, "Bookmarks"), "{\"roots\":{},\"version\":1,\"edited\":true}");
                var editedBytes = File.ReadAllBytes(Path.Combine(profileDirectory, "Bookmarks"));
                shell.SelectedTabIndex = ShellViewModel.RestoreTabIndex;
                var execution = restore.Execution;
                execution.CreateSession = (archive, pw, items, folder, safety, token) => BraveBookmarksRestoreSession.Create(archive, pw, items, folder, safety, () => ProcessPresence.Stopped, token);
                restore.SourcePath = shell.Options.BackupPath;
                for (var wait = 0; wait < 200 && !restore.Planning.HasSource; wait++) await Task.Delay(50);
                Check(restore.Planning.HasSource, "bookmarks archive validated on the Restore page");
                var row = restore.Planning.Groups.Single().Profiles.Single();
                Check(row.Categories.Single().IsSupported && !row.Categories.Single().IsSelected, "bookmarks category is enabled but starts unselected");
                var reviewButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Review restore"));
                Check(!reviewButton.IsEffectivelyEnabled, "Review restore starts disabled");
                row.IsSelected = true; row.SelectedInstallation = row.TargetInstallations[0]; row.SelectedProfile = row.TargetProfiles[0]; row.SelectAllSupportedCommand.Execute(null);
                restore.Planning.ObserveBrowserState = _ => ProcessPresence.Stopped; await restore.Planning.RecheckAsync();
                restore.Safety.Folder = Path.Combine(root, "output"); restore.Safety.Password = "Synthetic-safety-password"; restore.Safety.Confirmation = restore.Safety.Password;
                await Task.Delay(150); Check(reviewButton.IsEffectivelyEnabled, "Review restore enables once every prerequisite is met");
                Click(window, "Review restore");
                for (var wait = 0; wait < 400 && execution.Phase != RestorePhase.Reviewing; wait++) await Task.Delay(50);
                Check(execution.Phase == RestorePhase.Reviewing && File.ReadAllBytes(Path.Combine(profileDirectory, "Bookmarks")).AsSpan().SequenceEqual(editedBytes), "Review shows the plan and writes nothing");
                Check(window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text?.Contains("REPLACES") == true), "review text is displayed");
                restoreScroll.Offset = new Vector(0, restoreScroll.Extent.Height); await Task.Delay(250);
                using (var reviewBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                { reviewBitmap.Render(window); reviewBitmap.Save(Path.Combine(repo, "docs", "screenshots", "restore-execution-review.png"), PngBitmapEncoderOptions.Default); }
                Click(window, "Confirm restore");
                for (var wait = 0; wait < 600 && execution.Phase != RestorePhase.Completed; wait++) await Task.Delay(50);
                Check(execution.Phase == RestorePhase.Completed, "confirmed restore completes");
                Check(File.ReadAllBytes(Path.Combine(profileDirectory, "Bookmarks")).AsSpan().SequenceEqual(sampleBytes), "restore writes the exact backed-up bookmarks");
                await Task.Delay(200);
                Check(window.GetVisualDescendants().OfType<CheckBox>().Any(c => c.IsChecked == true && c.Content?.ToString()?.Contains("Bookmarks") == true), "checklist shows the restored component as checked");
                Check(window.GetVisualDescendants().OfType<ProgressBar>().Any(b => b.IsVisible && b.Value == 100), "progress bar reaches 100 percent");
                restoreScroll.Offset = new Vector(0, restoreScroll.Extent.Height); await Task.Delay(250);
                using (var doneBitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                { doneBitmap.Render(window); doneBitmap.Save(Path.Combine(repo, "docs", "screenshots", "restore-execution-completed.png"), PngBitmapEncoderOptions.Default); }
                Click(window, "Roll back this restore…"); await Task.Delay(100); Click(window, "Roll back");
                for (var wait = 0; wait < 600 && execution.Phase != RestorePhase.RolledBack; wait++) await Task.Delay(50);
                Check(execution.Phase == RestorePhase.RolledBack && File.ReadAllBytes(Path.Combine(profileDirectory, "Bookmarks")).AsSpan().SequenceEqual(editedBytes), "rollback restores the exact pre-restore bookmarks");
                Check(Directory.GetFiles(Path.Combine(root, "output"), "*.safety.encrypted").Length == 1, "encrypted safety backup is retained");
                Click(window, "Done"); await Task.Delay(100);
                Check(execution.Phase == RestorePhase.Idle && restore.Safety.Password.Length == 0, "Done clears the operation and safety secrets");
                Console.WriteLine("SCREENSHOT " + screenshot);
                desktop.Shutdown();
            }
            catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
        });
    }).StartWithClassicDesktopLifetime([]);

/// <summary>Records a value-free assertion, failing the desktop run on mismatch.</summary>
static void Check(bool condition, string label)
{ if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
/// <summary>Invokes a real dialog button through its Avalonia click handler.</summary>
static void Click(Window window, string label)
{
    var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
    typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);
}
/// <summary>Waits for a real owned dialog with a bounded deadline.</summary>
static async Task<Window> WaitDialog(IClassicDesktopStyleApplicationLifetime desktop, string title)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        var dialog = desktop.Windows.FirstOrDefault(w => w.Title == title);
        if (dialog is not null) { await Task.Delay(100); return dialog; }
        await Task.Delay(25);
    }
    throw new TimeoutException("Expected synthetic dialog: " + title);
}
