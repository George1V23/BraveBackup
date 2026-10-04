using System;
using System.IO;

namespace BraveBackup.Core;

public static class BraveProfileLocator
{
    public static string GetUserDataDirectory(string operatingSystem, string? localAppData = null, string? homeDirectory = null, string? applicationData = null)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem))
        {
            throw new ArgumentException("Operating system is required.", nameof(operatingSystem));
        }

        if (operatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            var localAppDataRoot = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrWhiteSpace(localAppDataRoot))
            {
                throw new InvalidOperationException("Unable to resolve the Windows local app data directory.");
            }

            return Path.Combine(localAppDataRoot, "BraveSoftware", "Brave-Browser", "User Data");
        }

        if (operatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedApplicationData = applicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrWhiteSpace(resolvedApplicationData))
            {
                return Path.Combine(resolvedApplicationData, "BraveSoftware", "Brave-Browser");
            }

            var userHome = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(userHome))
            {
                throw new InvalidOperationException("Unable to resolve the Linux home directory.");
            }

            return Path.Combine(userHome, ".config", "BraveSoftware", "Brave-Browser");
        }

        throw new PlatformNotSupportedException($"Unsupported operating system: {operatingSystem}");
    }
}
