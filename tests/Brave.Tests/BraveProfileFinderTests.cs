using BraveBackup.Core;
using Xunit;

namespace Brave.Tests;

public class BraveProfileFinderTests : IDisposable
{
    private readonly string _tempDirectory;

    public BraveProfileFinderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BraveBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void FindProfiles_WithLocalState_DiscoversProfilesAndResolvesNames()
    {
        // Arrange
        var localStateJson = """
            {
              "profile": {
                "info_cache": {
                  "Default": {
                    "name": "Work Profile"
                  },
                  "Profile 1": {
                    "name": "Personal Profile"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), localStateJson);

        var defaultDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));
        var profile1Dir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "Profile 1"));

        var bookmarksFile = Path.Combine(defaultDir.FullName, "Bookmarks");
        File.WriteAllText(bookmarksFile, "{}");

        var appInfo = new BraveAppInfo(BraveAppChannel.Stable, "1.0.0", "/usr/bin/brave", _tempDirectory);

        // Act
        var profiles = BraveProfileFinder.FindProfiles(appInfo);

        // Assert
        Assert.Equal(2, profiles.Count);

        var defaultProfile = profiles.Single(p => p.ProfileDirectory == defaultDir.FullName);
        Assert.Equal("Work Profile", defaultProfile.ProfileName);
        Assert.Equal(bookmarksFile, defaultProfile.BookmarksPath);
        Assert.True(BraveProfileFinder.HasBookmarks(defaultProfile));

        var profile1 = profiles.Single(p => p.ProfileDirectory == profile1Dir.FullName);
        Assert.Equal("Personal Profile", profile1.ProfileName);
        Assert.Equal(Path.Combine(profile1Dir.FullName, "Bookmarks"), profile1.BookmarksPath);
        Assert.False(BraveProfileFinder.HasBookmarks(profile1));
    }

    [Fact]
    public void FindProfiles_WithLocalState_IgnoresDirectoriesNotInLocalState()
    {
        // Arrange
        var localStateJson = """
            {
              "profile": {
                "info_cache": {
                  "Default": {
                    "name": "Default"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), localStateJson);

        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Profile 2"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "ShaderCache"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "GPUCache"));

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Single(profiles);
        Assert.Equal("Default", profiles[0].ProfileName);
    }

    [Fact]
    public void FindProfiles_WithLocalState_IgnoresProfilesWhoseDirectoriesDoNotExist()
    {
        // Arrange
        var localStateJson = """
            {
              "profile": {
                "info_cache": {
                  "Default": {
                    "name": "Default"
                  },
                  "Profile 1": {
                    "name": "Deleted Profile"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), localStateJson);

        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));
        // Profile 1 directory is intentionally not created

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Single(profiles);
        Assert.Equal("Default", profiles[0].ProfileName);
    }

    [Fact]
    public void FindProfiles_WithProfilesOrder_RespectsOrder()
    {
        // Arrange
        var localStateJson = """
            {
              "profile": {
                "info_cache": {
                  "Default": { "name": "Default Profile" },
                  "Profile 1": { "name": "First Profile" },
                  "Profile 2": { "name": "Second Profile" }
                },
                "profiles_order": ["Profile 2", "Profile 1", "Default"]
              }
            }
            """;
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), localStateJson);

        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Profile 1"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Profile 2"));

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Equal(3, profiles.Count);
        Assert.Equal("Second Profile", profiles[0].ProfileName);
        Assert.Equal("First Profile", profiles[1].ProfileName);
        Assert.Equal("Default Profile", profiles[2].ProfileName);
    }

    [Fact]
    public void FindProfiles_WithProfilesAttributesStorage_DiscoversProfiles()
    {
        // Arrange
        var localStateJson = """
            {
              "profile": {
                "profiles_attributes_storage": {
                  "Default": {
                    "name": "Custom Profile"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), localStateJson);
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Single(profiles);
        Assert.Equal("Custom Profile", profiles[0].ProfileName);
    }

    [Fact]
    public void FindProfiles_WithoutLocalState_FallsBackToDefaultAndNumberedProfiles()
    {
        // Arrange (no Local State file created)
        var defaultDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));
        var profile1Dir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "Profile 1"));

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.ProfileDirectory == defaultDir.FullName && p.ProfileName == "Default");
        Assert.Contains(profiles, p => p.ProfileDirectory == profile1Dir.FullName && p.ProfileName == "Profile 1");
    }

    [Fact]
    public void FindProfiles_WithCorruptLocalState_FallsBackGracefully()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDirectory, "Local State"), "{ not valid json ");
        var defaultDir = Directory.CreateDirectory(Path.Combine(_tempDirectory, "Default"));

        // Act
        var profiles = BraveProfileFinder.FindProfiles(_tempDirectory);

        // Assert
        Assert.Single(profiles);
        Assert.Equal("Default", profiles[0].ProfileName);
        Assert.Equal(defaultDir.FullName, profiles[0].ProfileDirectory);
    }

    [Fact]
    public void FindProfiles_WhenDirectoryDoesNotExist_ReturnsEmptyList()
    {
        // Arrange
        var nonExistentDir = Path.Combine(_tempDirectory, "NonExistent");

        // Act
        var profiles = BraveProfileFinder.FindProfiles(nonExistentDir);

        // Assert
        Assert.Empty(profiles);
    }

    [Fact]
    public void FindProfiles_NullAppInfo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => BraveProfileFinder.FindProfiles((BraveAppInfo)null!));
    }

    [Fact]
    public void HasBookmarks_NullProfile_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => BraveProfileFinder.HasBookmarks(null!));
    }
}
