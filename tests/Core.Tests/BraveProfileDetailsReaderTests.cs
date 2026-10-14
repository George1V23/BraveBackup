using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public class BraveProfileDetailsReaderTests : IDisposable
{
    private readonly string _tempDir;

    public BraveProfileDetailsReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "profile_details_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void GetExtensions_WhenExtensionsDirDoesNotExist_ReturnsEmptyList()
    {
        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var extensions = BraveProfileDetailsReader.GetExtensions(profile);

        Assert.Empty(extensions);
    }

    [Fact]
    public void GetExtensions_WhenExtensionsExistWithManifest_ParsesMetadata()
    {
        var extensionsDir = Path.Combine(_tempDir, "Extensions");
        var ext1Dir = Path.Combine(extensionsDir, "extension-id-1", "1.0.0_0");
        Directory.CreateDirectory(ext1Dir);

        var manifest = new
        {
            name = "Awesome AdBlocker",
            version = "1.0.0",
            description = "Blocks all ads on the web"
        };
        File.WriteAllText(Path.Combine(ext1Dir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var extensions = BraveProfileDetailsReader.GetExtensions(profile);

        Assert.Single(extensions);
        Assert.Equal("extension-id-1", extensions[0].Id);
        Assert.Equal("Awesome AdBlocker", extensions[0].Name);
        Assert.Equal("1.0.0", extensions[0].Version);
        Assert.Equal("Blocks all ads on the web", extensions[0].Description);
    }

    [Fact]
    public void GetExtensions_WhenManifestMissing_FallsBackToFolderNames()
    {
        var extensionsDir = Path.Combine(_tempDir, "Extensions");
        var ext1Dir = Path.Combine(extensionsDir, "custom-ext-id", "2.1.0");
        Directory.CreateDirectory(ext1Dir);

        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var extensions = BraveProfileDetailsReader.GetExtensions(profile);

        Assert.Single(extensions);
        Assert.Equal("custom-ext-id", extensions[0].Id);
        Assert.Equal("custom-ext-id", extensions[0].Name);
        Assert.Equal("2.1.0", extensions[0].Version);
        Assert.Null(extensions[0].Description);
    }

    [Fact]
    public void GetPasswordEntries_WhenNoLoginData_ReturnsEmptyList()
    {
        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var passwords = BraveProfileDetailsReader.GetPasswordEntries(profile);

        Assert.Empty(passwords);
    }

    [Fact]
    public void GetPasswordEntries_WhenLoginDataExists_ReturnsEntryMetadataWithoutSecrets()
    {
        var loginDataPath = Path.Combine(_tempDir, "Login Data");
        File.WriteAllBytes(loginDataPath,
            new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20 }); // SQLite header stub

        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var passwords = BraveProfileDetailsReader.GetPasswordEntries(profile);

        Assert.Single(passwords);
        Assert.Contains("Login Data", passwords[0].OriginUrl);
        Assert.Contains("protected", passwords[0].Username);
    }

    [Fact]
    public void GetPasswordEntries_WhenLoginDataJsonMetadataPresent_ParsesEntries()
    {
        var loginDataPath = Path.Combine(_tempDir, "Login Data");
        File.WriteAllText(loginDataPath, "dummy");

        var metadata = new[]
        {
            new
            {
                origin_url = "https://example.com/login",
                username_value = "user@example.com",
                date_created = "2026-01-01T12:00:00Z",
                date_last_used = "2026-02-01T12:00:00Z"
            }
        };
        File.WriteAllText(Path.Combine(_tempDir, "Login Data.json"), JsonSerializer.Serialize(metadata));

        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var passwords = BraveProfileDetailsReader.GetPasswordEntries(profile);

        Assert.Single(passwords);
        Assert.Equal("https://example.com/login", passwords[0].OriginUrl);
        Assert.Equal("user@example.com", passwords[0].Username);
        Assert.NotNull(passwords[0].DateCreatedUtc);
        Assert.NotNull(passwords[0].DateLastUsedUtc);
    }

    [Fact]
    public void GetSessionInfo_WhenNoSessionFiles_ReturnsZeroCounts()
    {
        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var session = BraveProfileDetailsReader.GetSessionInfo(profile);

        Assert.Equal(0, session.SessionFilesCount);
        Assert.Equal(0, session.TotalSizeBytes);
        Assert.Empty(session.FileNames);
    }

    [Fact]
    public void GetSessionInfo_WhenSessionsDirAndFilesExist_ReturnsSummary()
    {
        var sessionsDir = Path.Combine(_tempDir, "Sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllBytes(Path.Combine(sessionsDir, "Session_123456"), new byte[100]);
        File.WriteAllBytes(Path.Combine(sessionsDir, "Tabs_123456"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_tempDir, "Current Session"), new byte[50]);

        var profile = new BraveProfileInfo("Default", _tempDir, Path.Combine(_tempDir, "Bookmarks"));
        var session = BraveProfileDetailsReader.GetSessionInfo(profile);

        Assert.Equal(3, session.SessionFilesCount);
        Assert.Equal(350, session.TotalSizeBytes);
        Assert.Contains("Sessions/Session_123456", session.FileNames);
        Assert.Contains("Sessions/Tabs_123456", session.FileNames);
        Assert.Contains("Current Session", session.FileNames);
    }

    [Fact]
    public void ArgumentNullChecks_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => BraveProfileDetailsReader.GetExtensions((BraveProfileInfo)null!));
        Assert.Throws<ArgumentNullException>(() =>
            BraveProfileDetailsReader.GetPasswordEntries((BraveProfileInfo)null!));
        Assert.Throws<ArgumentNullException>(() => BraveProfileDetailsReader.GetSessionInfo((BraveProfileInfo)null!));
    }
}