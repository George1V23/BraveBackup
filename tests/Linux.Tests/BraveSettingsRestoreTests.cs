using System.Text.Json;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BraveSettingsRestoreTests : IDisposable
{
    private const string Stored = """
        {"bookmark_bar":{"show_on_all_tabs":false,"visibility_state":1},
         "browser":{"show_home_button":false},"homepage":"",
         "session":{"restore_on_startup":4,"startup_urls":["https://second.example.test","https://first.example.test"]}}
        """;

    private const string Original = """
        {
          "bookmark_bar": {"show_on_all_tabs":true,"neighbor":"keep"},
          "browser":{"show_home_button":true,"neighbor":[null,1]},
          "homepage":"https://before.example.test",
          "homepage_is_newtabpage":true,
          "session":{"restore_on_startup":1,"startup_urls":[],"exit_type":"Normal"},
          "extensions":{"settings":{"synthetic":{"state":1}}}
        }
        """;

    private const string Bookmarks = """
        {"version":1,"roots":{"bookmark_bar":{"name":"Bar","children":[]},"other":{"name":"Other","children":[]},"synced":{"name":"Mobile","children":[]}}}
        """;

    private const string Version = "1.102.13";
    private const string SafetyPassword = "synthetic-safety-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SettingsRestore_" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task VerifiedSettingsOnlyOrMixedRestorePreservesOtherValuesAndRollsBack(bool mixed, bool encrypted)
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateProfile("source", Stored);
        var destination = CreateProfile("destination", Original);
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        var password = encrypted ? "synthetic-archive-password" : null;
        string[] categories = mixed ? ["bookmarks", "settings"] : ["settings"];
        var (archive, manifest) = await CreateArchive(source, categories, password);
        var preferences = Path.Combine(destination.Profile.ProfileDirectory, "Preferences");
        var secure = Path.Combine(destination.Profile.ProfileDirectory, "Secure Preferences");
        File.WriteAllText(secure, """{"protection":{"macs":{"synthetic":true}},"extensions":{"settings":{}}}""");
        var secureBytes = File.ReadAllBytes(secure);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(preferences, mode);
        var originalBytes = File.ReadAllBytes(preferences);
        var originalBookmarks = Bookmarks + " ";
        File.WriteAllText(destination.Profile.BookmarksPath, originalBookmarks);

        using var session = Open(archive, manifest, password, destination, recovery, categories);
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(originalBytes, File.ReadAllBytes(preferences));

        var completed = new List<string>();
        session.Transaction.Progress = progress =>
        {
            if (progress.CategoryCompleted && progress.State == RestoreTransactionState.Running)
                completed.Add(progress.Category);
        };
        session.Transaction.Prepare();
        Assert.Equal(originalBytes, File.ReadAllBytes(preferences));
        session.Transaction.Run();

        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);
        Assert.Equal(categories, completed);
        Assert.Equal(mode, File.GetUnixFileMode(preferences));
        Assert.Equal(secureBytes, File.ReadAllBytes(secure));
        Assert.Equal(mixed ? Bookmarks : originalBookmarks, File.ReadAllText(destination.Profile.BookmarksPath));
        Assert.Equal(Stored, File.ReadAllText(Path.Combine(source.Profile.ProfileDirectory, "Preferences")));
        using var result = JsonDocument.Parse(File.ReadAllBytes(preferences));
        var root = result.RootElement;
        var bookmarkBar = root.GetProperty("bookmark_bar");
        Assert.False(bookmarkBar.GetProperty("show_on_all_tabs").GetBoolean());
        Assert.Equal(1, bookmarkBar.GetProperty("visibility_state").GetInt32());
        Assert.Equal("keep", bookmarkBar.GetProperty("neighbor").GetString());
        var browser = root.GetProperty("browser");
        Assert.False(browser.GetProperty("show_home_button").GetBoolean());
        Assert.Equal("[null,1]", browser.GetProperty("neighbor").GetRawText());
        Assert.Equal("", root.GetProperty("homepage").GetString());
        Assert.True(root.GetProperty("homepage_is_newtabpage").GetBoolean());
        var startup = root.GetProperty("session");
        Assert.Equal(4, startup.GetProperty("restore_on_startup").GetInt32());
        Assert.Equal(new[] { "https://second.example.test", "https://first.example.test" },
            startup.GetProperty("startup_urls").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Normal", startup.GetProperty("exit_type").GetString());
        Assert.Equal(1, root.GetProperty("extensions").GetProperty("settings").GetProperty("synthetic").GetProperty("state").GetInt32());

        session.Transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, session.Transaction.State);
        Assert.Equal(originalBytes, File.ReadAllBytes(preferences));
        Assert.Equal(mode, File.GetUnixFileMode(preferences));
        Assert.Equal(secureBytes, File.ReadAllBytes(secure));
        Assert.Equal(originalBookmarks, File.ReadAllText(destination.Profile.BookmarksPath));
        using var safety = File.OpenRead(session.Artifacts.SafetyPath);
        Assert.True(ArchiveEncryption.IsEncrypted(safety));
    }

    [Theory]
    [InlineData("protected", "restore-settings-protected")]
    [InlineData("wrong-parent", "restore-settings-parent")]
    [InlineData("wrong-value", "restore-settings-value")]
    [InlineData("missing", "restore-settings-missing")]
    [InlineData("empty-source", "restore-settings-empty")]
    public async Task UnsafeSettingsPlanRejectsEveryCategoryBeforePublishing(string condition, string expectedCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = CreateProfile("source", condition == "empty-source" ? "{}" : Stored);
        var destination = CreateProfile("destination", condition switch
        {
            "wrong-parent" => """{"browser":null}""",
            "wrong-value" => """{"browser":{"show_home_button":{"neighbor":"keep"}}}""",
            _ => Original
        });
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        var preferences = Path.Combine(destination.Profile.ProfileDirectory, "Preferences");
        if (condition == "missing") File.Delete(preferences);
        if (condition == "protected")
            File.WriteAllText(Path.Combine(destination.Profile.ProfileDirectory, "Secure Preferences"), """{"browser":{"show_home_button":true}}""");
        File.WriteAllText(destination.Profile.BookmarksPath, Bookmarks + " ");
        var before = Directory.GetFiles(destination.Profile.ProfileDirectory).ToDictionary(path => path, File.ReadAllBytes);
        string[] categories = ["bookmarks", "settings"];
        var (archive, manifest) = await CreateArchive(source, categories, null);

        var error = Assert.Throws<BackupOperationException>(() =>
            Open(archive, manifest, null, destination, recovery, categories));

        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(destination.Profile.ProfileDirectory).Order());
        foreach (var (path, content) in before) Assert.Equal(content, File.ReadAllBytes(path));
    }

    private (BraveAppInfo App, BraveProfileInfo Profile) CreateProfile(string name, string preferences)
    {
        var root = Path.Combine(_root, name);
        var directory = Directory.CreateDirectory(Path.Combine(root, "Default")).FullName;
        var bookmarks = Path.Combine(directory, "Bookmarks");
        File.WriteAllText(bookmarks, Bookmarks);
        File.WriteAllText(Path.Combine(directory, "Preferences"), preferences);
        return (new(BraveAppChannel.Stable, Version, "synthetic", root), new(name, directory, bookmarks));
    }

    private async Task<(string Path, ArchiveManifest Manifest)> CreateArchive(
        (BraveAppInfo App, BraveProfileInfo Profile) source, string[] categories, string? password)
    {
        var path = Path.Combine(_root, "source.bravebackup");
        await BraveArchiveBackup.CreateAsync([new(source.App, [source.Profile])], categories, path, password,
            "balanced", [source.App.UserDataDirectory], "synthetic", _ => Task.FromResult(false));
        using var archive = File.OpenRead(path);
        return (path, BraveSettingsBackup.ValidateArchive(archive, password));
    }

    private static LinuxRestoreSession Open(string path, ArchiveManifest manifest, string? password,
        (BraveAppInfo App, BraveProfileInfo Profile) destination, string recovery, string[] categories)
    {
        var source = manifest.Installations.Single().Profiles.Single().Id;
        return BraveProfileRestorePlan.Open(path, manifest.ArchiveId, password, recovery, SafetyPassword,
            categories.Select(category => new BraveRestoreMapping(source, destination.App, destination.Profile, category)).ToArray(),
            [destination.App.UserDataDirectory], () => ProcessPresence.Stopped);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
