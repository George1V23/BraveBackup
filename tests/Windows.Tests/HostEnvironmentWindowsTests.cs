using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Validates native Windows environment/registry services when run on Windows.</summary>
public sealed class HostEnvironmentWindowsTests
{
    [Fact]
    public void WindowsHost_UsesNativeDirectoriesAndCaseInsensitivePaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HostEnvironment.LocalApplicationData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), HostEnvironment.ProgramFiles);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), HostEnvironment.ProgramFilesX86);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, HostEnvironment.PathComparison);
        Assert.Null(HostEnvironment.ReadRegistryValue(@"HKEY_CURRENT_USER\Software\BraveBackupMissingTestKey", "missing"));
        Assert.Equal("Unknown", HostEnvironment.GetFileVersion(@"C:\BraveBackupMissingTestFile.exe"));
    }
}
