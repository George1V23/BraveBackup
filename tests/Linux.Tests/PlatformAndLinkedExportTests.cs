using System.Diagnostics;
using BraveBackup.Core;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Checks native Linux services and linked-path export protection using temporary fixtures.</summary>
public sealed class PlatformAndLinkedExportTests
{
    [Fact]
    public void MetadataCommand_PassesOneLiteralArgumentWithoutAShell()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string literal = "--version; $(this-must-not-run) with spaces";
        Assert.Equal(literal, HostEnvironment.ReadCommandOutput("/bin/echo", literal));
        Assert.Null(HostEnvironment.ReadCommandOutput("/bin/false", "--version"));
        Assert.Null(HostEnvironment.ReadCommandOutput("/missing-bravebackup-test-executable", "--version"));
        Assert.Null(HostEnvironment.ReadRegistryValue(@"HKEY_CURRENT_USER\Software\Missing", "value"));
    }

    [Fact]
    public void MetadataCommand_TimesOutAndTerminatesTheProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var timer = Stopwatch.StartNew();
        Assert.Null(HostEnvironment.ReadCommandOutput("/bin/sleep", "15"));
        Assert.InRange(timer.Elapsed.TotalSeconds, 2, 10);
    }

    [Fact]
    public void ConfigurationDirectory_UsesAbsoluteXdgOrHomeFallback()
    {
        if (!OperatingSystem.IsLinux()) return;
        var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var expected = !string.IsNullOrEmpty(configured) && Path.IsPathFullyQualified(configured)
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Assert.Equal(expected, HostEnvironment.ConfigurationDirectory);
        Assert.Equal(StringComparison.Ordinal, HostEnvironment.PathComparison);
        Assert.Equal((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries),
            HostEnvironment.ExecutableSearchPaths);
    }

    [Fact]
    public async Task Exports_RejectLinkedDirectoryAndFileWithoutTouchingTheSource()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "BraveLinkedExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            var source = Path.Combine(profile, "Bookmarks");
            File.WriteAllText(source, "original browser data");
            var directoryLink = Path.Combine(root, "linked-profile");
            Directory.CreateSymbolicLink(directoryLink, profile);
            var fileLink = Path.Combine(root, "linked-output.json");
            File.CreateSymbolicLink(fileLink, source);
            var danglingLink = Path.Combine(root, "dangling.json");
            File.CreateSymbolicLink(danglingLink, Path.Combine(profile, "missing-file"));
            var backup = new BackupModel(1, Array.Empty<BackupBookmark>());

            Assert.Throws<InvalidOperationException>(() => BraveBookmarksExporter.ExportToFile(backup, fileLink));
            Assert.Throws<InvalidOperationException>(() => BraveBookmarksExporter.ExportToFile(backup, danglingLink));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BraveBookmarksExporter.ExportToFileAsync(backup, Path.Combine(directoryLink, "nested", "backup.json")));
            Assert.Equal("original browser data", File.ReadAllText(source));
            Assert.False(Directory.Exists(Path.Combine(profile, "nested")));
            Assert.False(File.Exists(Path.Combine(profile, "missing-file")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Export_ReplacesHardLinkedOutputWithoutModifyingSourceInode()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "BraveHardLink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            var source = Path.Combine(profile, "Bookmarks");
            File.WriteAllText(source, "original browser data");
            var output = Path.Combine(root, "backup.json");
            using var link = new Process();
            link.StartInfo = new ProcessStartInfo("/bin/ln") { UseShellExecute = false };
            link.StartInfo.ArgumentList.Add(source);
            link.StartInfo.ArgumentList.Add(output);
            link.Start();
            Assert.True(link.WaitForExit(3000));
            Assert.Equal(0, link.ExitCode);

            BraveBookmarksExporter.ExportToFile(new BackupModel(1, Array.Empty<BackupBookmark>()), output);
            Assert.Equal("original browser data", File.ReadAllText(source));
            Assert.Contains("SchemaVersion", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
