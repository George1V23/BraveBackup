using System.Diagnostics;

namespace BraveBackup.Platform;

/// <summary>Distinguishes an observed match from a complete no-match scan and an indeterminate scan.</summary>
public enum ProcessPresence
{
    Stopped,
    Running,
    Unknown
}

/// <summary>Observes process names without launching commands, reading secrets or terminating processes.</summary>
public static class ProcessPresenceProbe
{
    /// <summary>Scans visible processes for caller-supplied executable-name prefixes; errors fail closed.</summary>
    public static ProcessPresence Observe(IReadOnlyList<string> prefixes, CancellationToken token = default)
    {
        if (prefixes.Count == 0 || prefixes.Any(string.IsNullOrWhiteSpace)) return ProcessPresence.Unknown;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return ProcessPresence.Unknown;
        }

        var unknown = false;
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (Matches(process.ProcessName, prefixes)) return ProcessPresence.Running;
                }
                catch
                {
                    try
                    {
                        if (!process.HasExited) unknown = true;
                    }
                    catch
                    {
                        unknown = true;
                    }
                }
            }

            return unknown ? ProcessPresence.Unknown : ProcessPresence.Stopped;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>Applies matching and indeterminate-name policy to a controlled process snapshot.</summary>
    public static ProcessPresence Assess(IEnumerable<string?> names, IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0 || prefixes.Any(string.IsNullOrWhiteSpace)) return ProcessPresence.Unknown;
        var unknown = false;
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) unknown = true;
            else if (Matches(name, prefixes)) return ProcessPresence.Running;
        }

        return unknown ? ProcessPresence.Unknown : ProcessPresence.Stopped;
    }

    private static bool Matches(string name, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals(prefix + ".exe", StringComparison.OrdinalIgnoreCase));
}