namespace BraveBackup.Platform;

/// <summary>Retains local Windows directory ancestors to prevent cooperating path replacement through delete sharing.</summary>
public sealed class WindowsRecoveryDirectoryChain : IDisposable
{
    private readonly WindowsRecoveryDirectoryIdentity[] _directories;
    private bool _disposed;
    private WindowsRecoveryDirectoryChain(WindowsRecoveryDirectoryIdentity[] directories) => _directories = directories;
    /// <summary>Opens each directory from local drive root to leaf. UNC/device paths are deliberately unsupported.</summary>
    public static WindowsRecoveryDirectoryChain Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is null || root.Length != 3 || root[1] != ':' || full.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Recovery ownership requires a supported local drive path.");
        var ancestors = new Stack<string>();
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        var owned = new List<WindowsRecoveryDirectoryIdentity>();
        try
        {
            foreach (var ancestor in ancestors) owned.Add(WindowsRecoveryDirectoryIdentity.Open(ancestor));
            var result = new WindowsRecoveryDirectoryChain(owned.ToArray()); result.Verify(); return result;
        }
        catch { foreach (var directory in owned.AsEnumerable().Reverse()) directory.Dispose(); throw; }
    }
    /// <summary>Revalidates every retained ancestor identity; does not access file payloads or create artifacts.</summary>
    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var directory in _directories) directory.Verify();
    }
    /// <summary>Releases ancestors in reverse order; existing recovery artifacts are untouched.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var directory in _directories.Reverse()) directory.Dispose();
    }
}
