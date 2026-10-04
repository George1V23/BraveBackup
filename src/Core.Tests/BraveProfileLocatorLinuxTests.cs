using Xunit;
using BraveBackup.Core;

namespace BraveBackup.Core.Tests;

public class BraveProfileLocatorLinuxTests
{
    [Fact]
    public void GetUserDataDirectory_Linux_WithApplicationData_ReturnsCorrectPath()
    {
        // Arrange
        var applicationData = "/home/testuser/.config";
        var operatingSystem = "linux";
        var expectedPath = "/home/testuser/.config/BraveSoftware/Brave-Browser";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: applicationData);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_WithoutApplicationData_FallsBackToHomeDirectory()
    {
        // Arrange
        var homeDirectory = "/home/testuser";
        var operatingSystem = "linux";
        var expectedPath = "/home/testuser/.config/BraveSoftware/Brave-Browser";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: null, homeDirectory: homeDirectory);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_WithoutApplicationData_ThrowsException()
    {
        // Arrange
        var operatingSystem = "linux";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: string.Empty, homeDirectory: string.Empty));

        Assert.Contains("Unable to resolve the Linux home directory", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_CaseInsensitive()
    {
        // Arrange
        var applicationData = "/home/testuser/.config";
        var operatingSystem = "LINUX";
        var expectedPath = "/home/testuser/.config/BraveSoftware/Brave-Browser";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: applicationData);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_WithVariousHomeDirectories()
    {
        // Arrange
        var testCases = new[]
        {
            ("/home/user1", "/home/user1/.config/BraveSoftware/Brave-Browser"),
            ("/home/admin", "/home/admin/.config/BraveSoftware/Brave-Browser"),
            ("/root", "/root/.config/BraveSoftware/Brave-Browser"),
        };

        // Act & Assert
        foreach (var (homeDirectory, expectedPath) in testCases)
        {
            var result = BraveProfileLocator.GetUserDataDirectory("linux", homeDirectory: homeDirectory);
            Assert.Equal(expectedPath, result);
        }
    }

    [Fact]
    public void GetUserDataDirectory_Linux_PathContainsBraveSoftwareDirectory()
    {
        // Arrange
        var applicationData = "/home/testuser/.config";
        var operatingSystem = "linux";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: applicationData);

        // Assert
        Assert.Contains("BraveSoftware", result);
        Assert.Contains("Brave-Browser", result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_UsesConfigDirectory()
    {
        // Arrange
        var homeDirectory = "/home/testuser";
        var operatingSystem = "linux";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, homeDirectory: homeDirectory);

        // Assert
        Assert.Contains(".config", result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_ApplicationDataTakesPrecedence()
    {
        // Arrange
        var applicationData = "/home/testuser/.config";
        var homeDirectory = "/root";
        var operatingSystem = "linux";
        var expectedPath = "/home/testuser/.config/BraveSoftware/Brave-Browser";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: applicationData, homeDirectory: homeDirectory);

        // Assert
        Assert.Equal(expectedPath, result);
        Assert.DoesNotContain("/root", result);
    }
}
