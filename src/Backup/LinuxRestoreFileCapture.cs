using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Bounded read-only original-state capture for Linux file-only recovery. No destination publication is performed.</summary>
public static class LinuxRestoreFileCapture
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    /// <summary>Captures a single child of an authorized directory, including original absence, rejecting observed source changes.</summary>
    /// <remarks>The caller must establish destination scope and stopped-browser state. This is not a complete transaction adapter.</remarks>
    public static RestoreFileImage Read(string directoryPath, string fileName, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!HostEnvironment.IsLinux) throw new PlatformNotSupportedException();
        if (string.IsNullOrEmpty(fileName) || fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("A single target filename is required.");
        using var directory = RecoveryDirectoryIdentity.Open(directoryPath);
        var path = Path.Combine(Path.GetFullPath(directoryPath), fileName);
        if (SnapshotFileAccess.Kind(path) == SnapshotNodeKind.Missing)
        {
            directory.Verify();
            if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.Missing) throw new IOException("Target absence changed during capture.");
            return new(false, []);
        }
        using var first = Capture(directory, fileName, token);
        using var second = Capture(directory, fileName, token);
        if (!first.Content.Bytes.Span.SequenceEqual(second.Content.Bytes.Span) || !first.Metadata.Bytes.Span.SequenceEqual(second.Metadata.Bytes.Span))
            throw new IOException("Restore target changed during capture.");
        directory.Verify(); token.ThrowIfCancellationRequested();
        return first.Clone();
    }
    private static RestoreFileImage Capture(RecoveryDirectoryIdentity directory, string name, CancellationToken token)
    {
        using var input = directory.OpenReadChild(name);
        var metadata = LinuxRecoveryMetadata.Capture(input.SafeFileHandle);
        if (input.Length > MaximumBytes) throw new IOException("Restore target exceeds the supported file limit.");
        var bytes = new byte[(int)input.Length];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new IOException("Restore target changed length.");
                offset += count;
            }
            if (input.ReadByte() != -1 || !LinuxRecoveryMetadata.Capture(input.SafeFileHandle).AsSpan().SequenceEqual(metadata))
                throw new IOException("Restore target changed during capture.");
            return new(true, bytes, metadata);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(metadata); }
    }
}
