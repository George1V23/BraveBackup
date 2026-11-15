using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BraveBackup.Core;

/// <summary>
/// Detects supported Brave Browser and Brave Origin installations on Windows and Linux.
/// </summary>
public static class BraveAppDetector
{
    /// <summary>
    /// Detects Brave installations at supported locations on the current operating system.
    /// Returned data directories are default paths and are not checked for existing profiles.
    /// </summary>
    /// <returns>A read-only list of detected <see cref="BraveAppInfo"/> records.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current OS is neither Windows nor Linux.</exception>
    public static IReadOnlyList<BraveAppInfo> DetectInstalledApps()
    {
        var isWindows = OperatingSystem.IsWindows();
        var isLinux = OperatingSystem.IsLinux();

        if (isWindows)
        {
            return DetectWindowsApps(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                GetRegistryValue, File.Exists, GetFileVersion);
        }

        if (isLinux)
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            return DetectLinuxApps(paths, config, File.Exists, path => GetLinuxVersion(path, ""));
        }

        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }

    /// <summary>
    /// Detects standard and Origin installations across Stable, Beta, and Nightly Windows locations.
    /// The registry candidate is checked before the standard installation roots for each product and channel.
    /// </summary>
    /// <param name="localAppData">Local application data root, also used to construct default user-data paths.</param>
    /// <param name="programFiles">Program Files installation root.</param>
    /// <param name="programFilesX86">Program Files (x86) installation root.</param>
    /// <param name="registry">Reads a registry value by key path and value name, or returns null.</param>
    /// <param name="exists">Tests whether a candidate executable exists.</param>
    /// <param name="version">Obtains the version of a detected executable.</param>
    /// <returns>One entry per detected product and channel, with its default user-data path.</returns>
    internal static IReadOnlyList<BraveAppInfo> DetectWindowsApps(string localAppData, string programFiles,
        string programFilesX86, Func<string, string, string?> registry, Func<string, bool> exists, Func<string, string> version)
    {
        var results = new List<BraveAppInfo>();
        foreach (var product in new[] { BraveAppProduct.Browser, BraveAppProduct.Origin })
        foreach (var channel in new[] { BraveAppChannel.Stable, BraveAppChannel.Beta, BraveAppChannel.Nightly })
        {
            var name = ProductDirectory(product, channel);
            var key = $"HKEY_CURRENT_USER\\Software\\BraveSoftware\\{name}";
            var directory = registry(key, "installation directory");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory, "brave.exe"));
            foreach (var root in new[] { localAppData, programFiles, programFilesX86 }.Where(root => !string.IsNullOrWhiteSpace(root)))
                candidates.Add(Path.Combine(root, "BraveSoftware", name, "Application", "brave.exe"));
            var executable = candidates.FirstOrDefault(exists);
            if (executable != null)
                results.Add(new BraveAppInfo(channel, version(executable), executable,
                    Path.Combine(localAppData, "BraveSoftware", name, "User Data")) { Product = product });
        }
        return results.AsReadOnly();
    }

    /// <summary>
    /// Detects standard Brave channels and the stable Origin launcher on Linux.
    /// Searches common launcher directories before the supplied paths and supported release fallbacks.
    /// Alternative launcher names for a channel yield a single result.
    /// </summary>
    /// <param name="paths">Directories to search for launcher commands.</param>
    /// <param name="configDirectory">Configuration root used to construct default user-data paths.</param>
    /// <param name="exists">Tests whether a candidate executable exists.</param>
    /// <param name="version">Obtains the version of a detected executable.</param>
    /// <returns>One entry per detected product and channel, with its default user-data path.</returns>
    internal static IReadOnlyList<BraveAppInfo> DetectLinuxApps(IEnumerable<string> paths, string configDirectory,
        Func<string, bool> exists, Func<string, string> version)
    {
        var results = new List<BraveAppInfo>();
        var definitions = new[]
        {
            (BraveAppProduct.Browser, BraveAppChannel.Stable, new[] { "brave-browser", "brave" }, "/opt/brave.com/brave/brave"),
            (BraveAppProduct.Browser, BraveAppChannel.Beta, new[] { "brave-browser-beta", "brave-beta" }, ""),
            (BraveAppProduct.Browser, BraveAppChannel.Nightly, new[] { "brave-browser-nightly", "brave-nightly" }, ""),
            (BraveAppProduct.Origin, BraveAppChannel.Stable, new[] { "brave-origin" }, "/opt/brave.com/brave-origin/brave")
        };
        foreach (var (product, channel, commands, fallback) in definitions)
        {
            var candidates = new[] { "/usr/bin", "/usr/local/bin", "/snap/bin" }.Concat(paths)
                .SelectMany(path => commands.Select(command => Path.Combine(path, command)));
            if (!string.IsNullOrEmpty(fallback)) candidates = candidates.Append(fallback);
            var executable = candidates.FirstOrDefault(exists);
            if (executable != null)
                results.Add(new BraveAppInfo(channel, version(executable), executable,
                    Path.Combine(configDirectory, "BraveSoftware", ProductDirectory(product, channel))) { Product = product });
        }
        return results.AsReadOnly();
    }

    /// <summary>Builds the default product directory name for a product and release channel.</summary>
    /// <param name="product">The Brave product.</param>
    /// <param name="channel">The release channel.</param>
    /// <returns>The product directory name, including any channel suffix.</returns>
    private static string ProductDirectory(BraveAppProduct product, BraveAppChannel channel)
    {
        var name = product == BraveAppProduct.Origin ? "Brave-Origin" : "Brave-Browser";
        return name + (channel switch { BraveAppChannel.Beta => "-Beta", BraveAppChannel.Nightly => "-Nightly", _ => "" });
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
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return "Unknown";
            }
            var output = outputTask.GetAwaiter().GetResult().Trim();
            errorTask.GetAwaiter().GetResult();

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
                    Arguments = $"query \"{keyPath}\" /v \"{valueName}\"",
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
