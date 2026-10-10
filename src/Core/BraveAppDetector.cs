using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BraveBackup.Core;

/// <summary>
/// Provides cross-platform detection of installed Brave Browser instances (Stable, Beta, Nightly).
/// </summary>
public static class BraveAppDetector
{
    /// <summary>
    /// Detects all installed Brave Browser applications on the current operating system.
    /// </summary>
    /// <returns>A read-only list of detected <see cref="BraveAppInfo"/> records.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current OS is neither Windows nor Linux.</exception>
    public static IReadOnlyList<BraveAppInfo> DetectInstalledApps()
    {
        var isWindows = OperatingSystem.IsWindows();
        var isLinux = OperatingSystem.IsLinux();

        if (isWindows)
        {
            return DetectWindowsApps();
        }

        if (isLinux)
        {
            return DetectLinuxApps();
        }

        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }

    static IReadOnlyList<BraveAppInfo> DetectWindowsApps()
    {
        var results = new List<BraveAppInfo>();

        // Check registry for installed Brave versions
        var registryPaths = new[]
        {
            // Release (Stable)
            ("HKEY_CURRENT_USER\\Software\\BraveSoftware\\Brave-Browser", Release: BraveAppChannel.Stable),
            // Beta
            ("HKEY_CURRENT_USER\\Software\\BraveSoftware\\Brave-Browser-Beta", BraveAppChannel.Beta),
            // Nightly (Dev)
            ("HKEY_CURRENT_USER\\Software\\BraveSoftware\\Brave-Browser-Nightly", BraveAppChannel.Nightly)
        };

        foreach (var (regPath, channel) in registryPaths)
        {
            try
            {
                var version = GetRegistryValue(regPath, "version");
                var installPath = GetRegistryValue(regPath, "installation directory");

                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                {
                    var programPath = Path.Combine(installPath, "brave.exe");
                    var userDataDir = GetWindowsUserDataDirectory(channel);

                    if (File.Exists(programPath))
                    {
                        results.Add(new BraveAppInfo(
                            channel,
                            version ?? "Unknown",
                            programPath,
                            userDataDir));
                    }
                }
            }
            catch
            {
                // Registry key may not exist, skip this channel
            }
        }

        // Fallback: check common installation locations
        if (results.Count == 0)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var fallbackPaths = new[]
            {
                (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"), Release: BraveAppChannel.Stable),
                (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser-Beta", "Application", "brave.exe"), BraveAppChannel.Beta),
                (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser-Nightly", "Application", "brave.exe"), BraveAppChannel.Nightly),
                (Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"), Release: BraveAppChannel.Stable),
                (Path.Combine(programFiles, "BraveSoftware", "Brave-Browser-Beta", "Application", "brave.exe"), BraveAppChannel.Beta),
                (Path.Combine(programFiles, "BraveSoftware", "Brave-Browser-Nightly", "Application", "brave.exe"), BraveAppChannel.Nightly)
            };

            var addedChannels = new HashSet<BraveAppChannel>(results.Select(r => r.Channel));

            foreach (var (exePath, channel) in fallbackPaths)
            {
                if (addedChannels.Contains(channel)) continue;

                if (File.Exists(exePath))
                {
                    var version = GetFileVersion(exePath);
                    var userDataDir = GetWindowsUserDataDirectory(channel);

                    results.Add(new BraveAppInfo(
                        channel,
                        version,
                        exePath,
                        userDataDir));
                    addedChannels.Add(channel);
                }
            }
        }

        return results.AsReadOnly();
    }

    static string GetWindowsUserDataDirectory(BraveAppChannel channel)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var subDir = channel switch
        {
            BraveAppChannel.Stable => "BraveSoftware\\Brave-Browser\\User Data",
            BraveAppChannel.Beta => "BraveSoftware\\Brave-Browser-Beta\\User Data",
            BraveAppChannel.Nightly => "BraveSoftware\\Brave-Browser-Nightly\\User Data",
            _ => "BraveSoftware\\Brave-Browser\\User Data"
        };

        return Path.Combine(localAppData, subDir);
    }

    static IReadOnlyList<BraveAppInfo> DetectLinuxApps()
    {
        var results = new List<BraveAppInfo>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var channels = new[]
        {
            (Release: BraveAppChannel.Stable, "brave-browser", "brave-browser"),
            (BraveAppChannel.Beta, "brave-browser-beta", "brave-browser-beta"),
            (BraveAppChannel.Nightly, "brave-browser-nightly", "brave-browser-nightly")
        };

        foreach (var (channel, commandName, appName) in channels)
        {
            // Try to find the executable using 'which' or common paths
            var programPath = FindLinuxExecutable(commandName);

            if (string.IsNullOrEmpty(programPath))
            {
                // Check common installation paths
                var commonPaths = new[]
                {
                    $"/usr/bin/{commandName}",
                    $"/usr/local/bin/{commandName}",
                    $"/opt/brave.com/{appName}/{commandName}",
                    $"/snap/bin/{commandName}"
                };

                foreach (var path in commonPaths)
                {
                    if (File.Exists(path))
                    {
                        programPath = path;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(programPath) && File.Exists(programPath))
            {
                var version = GetLinuxVersion(programPath, commandName);
                var userDataDir = GetLinuxUserDataDirectory(home, channel);

                results.Add(new BraveAppInfo(
                    channel,
                    version,
                    programPath,
                    userDataDir));
            }
        }

        return results.AsReadOnly();
    }

    static string? FindLinuxExecutable(string commandName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = commandName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                return output;
            }
        }
        catch
        {
            // 'which' command may not be available
        }

        return null;
    }

    static string GetLinuxVersion(string programPath, string commandName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = programPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                // Extract version number from output like "Brave-Browser 1.60.126 Chromium: 120.0.6099.109"
                var match = Regex.Match(output, @"(\d+\.\d+\.\d+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }

                return output;
            }
        }
        catch
        {
            // Version detection failed
        }

        return "Unknown";
    }

    static string GetLinuxUserDataDirectory(string home, BraveAppChannel channel)
    {
        var configDir = Path.Combine(home, ".config");
        var subDir = channel switch
        {
            BraveAppChannel.Stable => "BraveSoftware/Brave-Browser",
            BraveAppChannel.Beta => "BraveSoftware/Brave-Browser-Beta",
            BraveAppChannel.Nightly => "BraveSoftware/Brave-Browser-Nightly",
            _ => "BraveSoftware/Brave-Browser"
        };

        return Path.Combine(configDir, subDir);
    }

    static string? GetRegistryValue(string keyPath, string valueName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "reg",
                    Arguments = $"query \"{keyPath}\" /v {valueName}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            return parts[2];
                        }
                    }
                }
            }
        }
        catch
        {
            // Registry query failed
        }

        return null;
    }

    static string GetFileVersion(string exePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            var version = versionInfo.ProductVersion;
            return string.IsNullOrEmpty(version) ? "Unknown" : version;
        }
        catch
        {
            return "Unknown";
        }
    }
}
