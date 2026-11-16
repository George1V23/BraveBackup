using System;
using System.Collections.Generic;
using BraveBackup.Platform;
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
        var isWindows = HostEnvironment.IsWindows;
        var isLinux = HostEnvironment.IsLinux;

        if (isWindows)
        {
            return DetectWindowsApps(
                HostEnvironment.LocalApplicationData,
                HostEnvironment.ProgramFiles,
                HostEnvironment.ProgramFilesX86,
                HostEnvironment.ReadRegistryValue, File.Exists, HostEnvironment.GetFileVersion);
        }

        if (isLinux)
        {
            return DetectLinuxApps(HostEnvironment.ExecutableSearchPaths, HostEnvironment.ConfigurationDirectory,
                File.Exists, path => ParseVersion(HostEnvironment.ReadCommandOutput(path, "--version")));
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

    /// <summary>Extracts a browser version from successful launcher output.</summary>
    /// <param name="output">The launcher output, or null when it could not be read.</param>
    /// <returns>The numeric version, the original output, or Unknown.</returns>
    private static string ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "Unknown";
        var match = Regex.Match(output, @"(\d+\.\d+\.\d+)");
        return match.Success ? match.Value : output.Trim();
    }

    /// <summary>Builds the default Brave product and channel directory name.</summary>
    private static string ProductDirectory(BraveAppProduct product, BraveAppChannel channel)
    {
        var name = product == BraveAppProduct.Origin ? "Brave-Origin" : "Brave-Browser";
        return name + (channel switch { BraveAppChannel.Beta => "-Beta", BraveAppChannel.Nightly => "-Nightly", _ => "" });
    }

}
