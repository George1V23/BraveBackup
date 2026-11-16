using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BraveBackup.Platform;

/// <summary>Host-specific private file creation and destination-folder opening for local backups.</summary>
public static class BackupFileOperations
{
    /// <summary>Creates an exclusive new temporary file with user-only permissions at creation time.</summary>
    public static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("Cannot determine the current user.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 65536, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 65536, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    /// <summary>Opens an existing folder through the host desktop without shell interpolation.</summary>
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true }
            : new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start) ?? throw new IOException("Could not open the destination folder.");
    }
}
