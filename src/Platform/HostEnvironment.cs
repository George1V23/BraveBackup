using System.Diagnostics;
using Microsoft.Win32;

namespace BraveBackup.Platform;

/// <summary>Provides operating-system services without browser-specific paths or formats.</summary>
public static class HostEnvironment
{
    /// <summary>Gets whether the host is Windows.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Gets whether the host is Linux.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Gets the host's local application-data directory.</summary>
    public static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Gets the host's application-data directory.</summary>
    public static string ApplicationData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Gets the user's configured Documents directory, or an empty value when unavailable.</summary>
    public static string DocumentsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Gets the current user's home directory.</summary>
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Gets the Windows Program Files directory, when available.</summary>
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>Gets the Windows Program Files (x86) directory, when available.</summary>
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>Gets the filesystem path comparison appropriate for the supported host.</summary>
    public static StringComparison PathComparison =>
        IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Gets the absolute XDG configuration root, falling back to the user's .config directory.</summary>
    public static string ConfigurationDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return !string.IsNullOrEmpty(configured) && Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(UserProfile, ".config");
        }
    }

    /// <summary>Gets executable search directories using the host's PATH separator.</summary>
    public static IReadOnlyList<string> ExecutableSearchPaths =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Reads a Windows registry string; returns null on other hosts or inaccessible values.</summary>
    /// <param name="keyPath">The full registry key path.</param>
    /// <param name="valueName">The registry value name.</param>
    public static string? ReadRegistryValue(string keyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Registry.GetValue(keyPath, valueName, null) as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Reads an executable's product version, returning Unknown when unavailable.</summary>
    /// <param name="executablePath">The executable to inspect without launching it.</param>
    public static string GetFileVersion(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            return string.IsNullOrEmpty(version) ? "Unknown" : version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Unknown";
        }
    }

    /// <summary>Runs a metadata command without a shell, returning output only on successful exit.</summary>
    /// <param name="executablePath">The program to launch.</param>
    /// <param name="argument">A single argument, passed without shell interpretation.</param>
    /// <remarks>Both output streams are drained concurrently. Commands exceeding three seconds are terminated.</remarks>
    public static string? ReadCommandOutput(string executablePath, string argument)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // Descendants may inherit the pipes after the launched command exits.
            if (!Task.WhenAll(output, error).Wait(TimeSpan.FromSeconds(3))) return null;
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception or AggregateException)
        {
            return null;
        }
    }
}