using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BraveBackup.Core;

/// <summary>
/// Provides utility methods to resolve Brave Browser default User Data directory paths for Windows and Linux.
/// </summary>
public static class BraveProfileLocator
{
    /// <summary>
    /// Resolves the User Data directory for the specified operating system and directory overrides.
    /// </summary>
    /// <param name="operatingSystem">The target operating system name ("windows" or "linux").</param>
    /// <param name="localAppData">Optional override for Windows LocalAppData path.</param>
    /// <param name="homeDirectory">Optional override for Linux user home path.</param>
    /// <param name="applicationData">Optional override for Linux ApplicationData path.</param>
    /// <returns>The resolved User Data directory path.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="operatingSystem"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when necessary environment paths cannot be resolved.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when an unsupported operating system is specified.</exception>
    public static string GetUserDataDirectory(string operatingSystem, string? localAppData = null, string? homeDirectory = null, string? applicationData = null)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem))
        {
            throw new ArgumentException("Operating system is required.", nameof(operatingSystem));
        }

        var isWindows = operatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase);
        if (isWindows)
        {
            if (localAppData is not null)
            {
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    throw new InvalidOperationException("Unable to resolve the Windows local app data directory.");
                }

                return CombinePaths(localAppData, true, "BraveSoftware", "Brave-Browser", "User Data");
            }

            var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppDataRoot))
            {
                throw new InvalidOperationException("Unable to resolve the Windows local app data directory.");
            }

            return CombinePaths(localAppDataRoot, true, "BraveSoftware", "Brave-Browser", "User Data");
        }

        if (operatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            if (applicationData is not null)
            {
                if (string.IsNullOrWhiteSpace(applicationData))
                {
                    if (!string.IsNullOrWhiteSpace(homeDirectory))
                    {
                        return CombinePaths(homeDirectory, false, ".config", "BraveSoftware", "Brave-Browser");
                    }

                    throw new InvalidOperationException("Unable to resolve the Linux home directory.");
                }

                return CombinePaths(applicationData, false, "BraveSoftware", "Brave-Browser");
            }

            if (homeDirectory is not null)
            {
                if (string.IsNullOrWhiteSpace(homeDirectory))
                {
                    throw new InvalidOperationException("Unable to resolve the Linux home directory.");
                }

                return CombinePaths(homeDirectory, false, ".config", "BraveSoftware", "Brave-Browser");
            }

            var resolvedApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(resolvedApplicationData))
            {
                return CombinePaths(resolvedApplicationData, false, "BraveSoftware", "Brave-Browser");
            }

            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userHome))
            {
                throw new InvalidOperationException("Unable to resolve the Linux home directory.");
            }

            return CombinePaths(userHome, false, ".config", "BraveSoftware", "Brave-Browser");
        }

        throw new PlatformNotSupportedException($"Unsupported operating system: {operatingSystem}");
    }

    private static string CombinePaths(string rootPath, bool isWindows, params string[] segments)
    {
        var normalizedRoot = NormalizePath(rootPath, isWindows);
        var combined = new List<string> { normalizedRoot };
        combined.AddRange(segments.Select(segment => NormalizeSegment(segment, isWindows)));

        var separator = isWindows ? "\\" : "/";
        var result = string.Join(separator, combined.Where(part => !string.IsNullOrEmpty(part)));
        return result.TrimEnd(separator[0]);
    }

    private static string NormalizePath(string path, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var separator = isWindows ? '\\' : '/';
        var alternateSeparator = isWindows ? '/' : '\\';
        var normalized = path.Replace(alternateSeparator, separator);

        while (normalized.Contains(separator.ToString() + separator))
        {
            normalized = normalized.Replace(separator.ToString() + separator, separator.ToString());
        }

        return normalized.TrimEnd(separator);
    }

    private static string NormalizeSegment(string segment, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var separator = isWindows ? '\\' : '/';
        var alternateSeparator = isWindows ? '/' : '\\';
        var normalized = segment.Replace(alternateSeparator, separator).Trim(separator);
        return normalized;
    }
}
