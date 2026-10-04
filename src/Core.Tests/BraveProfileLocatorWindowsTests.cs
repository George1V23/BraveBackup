using Xunit;
using BraveBackup.Core;

namespace BraveBackup.Core.Tests;

public class BraveProfileLocatorWindowsTests
{
    [Fact]
    public void GetUserDataDirectory_Windows_ReturnsCorrectPath()
    {
        // Arrange
        var localAppData = @"C:\Users\TestUser\AppData\Local";
        var operatingSystem = "windows";
        var expectedPath = @"C:\Users\TestUser\AppData\Local\BraveSoftware\Brave-Browser\User Data";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, localAppData);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetUserDataDirectory_Windows_WithoutLocalAppData_ThrowsException()
    {
        // Arrange
        var operatingSystem = "windows";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BraveProfileLocator.GetUserDataDirectory(operatingSystem, string.Empty));

        Assert.Contains("Unable to resolve the Windows local app data directory", exception.Message);
    }

    [Fact]
    public void GetUserDataDirectory_Windows_CaseInsensitive()
    {
        // Arrange
        var localAppData = @"C:\Users\TestUser\AppData\Local";
        var operatingSystem = "WINDOWS";
        var expectedPath = @"C:\Users\TestUser\AppData\Local\BraveSoftware\Brave-Browser\User Data";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, localAppData);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void GetUserDataDirectory_Windows_WithVariousLocalAppDataPaths()
    {
        // Arrange
        var testCases = new[]
        {
            (@"C:\Users\Admin\AppData\Local", @"C:\Users\Admin\AppData\Local\BraveSoftware\Brave-Browser\User Data"),
            (@"D:\Custom\AppData", @"D:\Custom\AppData\BraveSoftware\Brave-Browser\User Data"),
            (@"E:\Users\John\Local", @"E:\Users\John\Local\BraveSoftware\Brave-Browser\User Data"),
        };

        // Act & Assert
        foreach (var (localAppData, expectedPath) in testCases)
        {
            var result = BraveProfileLocator.GetUserDataDirectory("windows", localAppData);
            Assert.Equal(expectedPath, result);
        }
    }

    [Fact]
    public void GetUserDataDirectory_Windows_PathContainsBraveSoftwareDirectory()
    {
        // Arrange
        var localAppData = @"C:\Users\TestUser\AppData\Local";
        var operatingSystem = "windows";

        // Act
        var result = BraveProfileLocator.GetUserDataDirectory(operatingSystem, localAppData);

        // Assert
        Assert.Contains("BraveSoftware", result);
        Assert.Contains("Brave-Browser", result);
        Assert.Contains("User Data", result);
    }
}
