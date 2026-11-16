using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Rejects export paths inside browser data and paths traversing filesystem links.</summary>
public static class ExportDestinationGuard
{
    /// <summary>Checks an export destination before any directory or file is created.</summary>
    /// <param name="filePath">An absolute or relative output filename.</param>
    /// <param name="protectedDirectories">Additional user-data or profile directories known to the caller.</param>
    /// <returns>The normalized absolute destination path.</returns>
    /// <exception cref="InvalidOperationException">The path is within protected browser data or traverses a link.</exception>
    /// <remarks>Standard product roots and profile markers protect direct exporter callers too.
    /// Callers must supply custom roots that cannot be recognized from existing files.</remarks>
    public static string Validate(string filePath, IEnumerable<string>? protectedDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var roots = DefaultDataDirectories().Concat(protectedDirectories ?? Array.Empty<string>());
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (string.Equals(fullPath, fullRoot, HostEnvironment.PathComparison) ||
                fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
                    HostEnvironment.PathComparison))
                throw new InvalidOperationException("Export destination must be outside browser user-data and profile directories.");
        }

        // Check the final entry as well as every ancestor, including dangling links.
        RejectLink(fullPath);
        for (var directory = Path.GetDirectoryName(fullPath); directory != null; directory = Path.GetDirectoryName(directory))
        {
            RejectLink(directory);
            if (new[] { "Local State", "Preferences", "Secure Preferences", "Bookmarks", "Login Data" }
                .Any(marker => File.Exists(Path.Combine(directory, marker))))
                throw new InvalidOperationException("Export destination must be outside browser user-data and profile directories.");
        }
        return fullPath;
    }

    /// <summary>Rejects symbolic links and Windows reparse points conservatively.</summary>
    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Export destination must not traverse symbolic links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Enumerates standard product/channel data roots even when no executable is detected.</summary>
    private static IEnumerable<string> DefaultDataDirectories()
    {
        var configRoots = HostEnvironment.IsWindows
            ? new[] { HostEnvironment.LocalApplicationData }
            : new[] { HostEnvironment.ConfigurationDirectory, Path.Combine(HostEnvironment.UserProfile, ".config") };
        foreach (var root in configRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        foreach (var product in new[] { "Brave-Browser", "Brave-Origin" })
        foreach (var suffix in new[] { "", "-Beta", "-Nightly" })
        {
            var productRoot = Path.Combine(root, "BraveSoftware", product + suffix);
            yield return HostEnvironment.IsWindows ? Path.Combine(productRoot, "User Data") : productRoot;
        }
    }
}
