using BraveBackup.Core;
using Xunit;

namespace Brave.Tests;

public class BraveProfileLocatorEdgeCasesTests
{
    [Fact]
    public void GetUserDataDirectory_NullOperatingSystem_ThrowsException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            BraveProfileLocator.GetUserDataDirectory(null!));

        Assert.Contains("Operating system is required", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_EmptyOperatingSystem_ThrowsException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            BraveProfileLocator.GetUserDataDirectory(string.Empty));

        Assert.Contains("Operating system is required", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_WhitespaceOperatingSystem_ThrowsException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            BraveProfileLocator.GetUserDataDirectory("   "));

        Assert.Contains("Operating system is required", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_UnsupportedOperatingSystem_ThrowsException()
    {
        // Arrange
        var operatingSystem = "macos";

        // Act & Assert
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            BraveProfileLocator.GetUserDataDirectory(operatingSystem));

        Assert.Contains("Unsupported operating system", exception.Message);
        Assert.Contains("macos", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_UnsupportedOperatingSystemVariant_ThrowsException()
    {
        // Arrange
        var operatingSystem = "freebsd";

        // Act & Assert
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            BraveProfileLocator.GetUserDataDirectory(operatingSystem));

        Assert.Contains("Unsupported operating system", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_Windows_WithNullLocalAppData_UsesEnvironmentVariable()
    {
        // Arrange
        var operatingSystem = "windows";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, null);

        // Assert
        // Should not throw and should return a valid path
        Assert.NotNull(result);
        Assert.Contains("BraveSoftware", result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_WithNullApplicationData_FallsBackToHome()
    {
        // Arrange
        var operatingSystem = "linux";
        var homeDirectory = "/home/testuser";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, homeDirectory: homeDirectory);

        // Assert
        Assert.NotNull(result);
        Assert.Contains(".config", result);
    }

    [Fact]
    public void GetUserDataDirectory_Windows_ReturnsNormalizedPath()
    {
        // Arrange
        var localAppData = @"C:\Users\TestUser\AppData\Local\";
        var operatingSystem = "windows";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, localAppData);

        // Assert
        Assert.NotNull(result);
        // Path should be well-formed without double backslashes
        Assert.DoesNotContain(@"\\", result);
    }

    [Fact]
    public void GetUserDataDirectory_Linux_ReturnsNormalizedPath()
    {
        // Arrange
        var applicationData = "/home/testuser/.config/";
        var operatingSystem = "linux";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, applicationData: applicationData);

        // Assert
        Assert.NotNull(result);
        // Path should be well-formed without double slashes
        Assert.DoesNotContain("//", result);
    }
}
