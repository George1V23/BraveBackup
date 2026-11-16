using System.Diagnostics;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class SnapshotPlatformTests
{
    [Fact]
    public void ProcessPolicy_UsesBoundaryMatchingAndFailsClosedOnUnknownNames()
    {
        Assert.Equal(ProcessPresence.Stopped,
            ProcessPresenceProbe.Assess(new[] { "BraveBackup", "bravery", "dotnet" }, new[] { "brave" }));
        Assert.Equal(ProcessPresence.Running, ProcessPresenceProbe.Assess(new[] { "BRAVE.exe" }, new[] { "brave" }));
        Assert.Equal(ProcessPresence.Running,
            ProcessPresenceProbe.Assess(new[] { "brave-browser-n" }, new[] { "brave" }));
        Assert.Equal(ProcessPresence.Unknown,
            ProcessPresenceProbe.Assess(new string?[] { null, "unrelated" }, new[] { "brave" }));
        Assert.Equal(ProcessPresence.Unknown,
            ProcessPresenceProbe.Assess(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void NativeLinuxProbe_SeesAnIsolatedChildAndThenItsExit()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "Probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = "bbp" + Guid.NewGuid().ToString("N")[..8];
        var executable = Path.Combine(directory, name);
        File.Copy("/usr/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var process = Process.Start(new ProcessStartInfo(executable, "30") { UseShellExecute = false })!;
        try
        {
            Assert.Equal(ProcessPresence.Running, ProcessPresenceProbe.Observe(new[] { name }));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit();
            Directory.Delete(directory, true);
        }

        Assert.Equal(ProcessPresence.Stopped, ProcessPresenceProbe.Observe(new[] { name }));
    }

    [Fact]
    public void FileInspection_DistinguishesRegularMissingDirectoryLinksAndLinuxFifo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Node_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "file.bin");
            File.WriteAllBytes(file, new byte[] { 0, 255 });
            Assert.Equal(SnapshotNodeKind.Directory, SnapshotFileAccess.Kind(directory));
            Assert.Equal(SnapshotNodeKind.Missing, SnapshotFileAccess.Kind(Path.Combine(directory, "missing")));
            using (var input = SnapshotFileAccess.OpenRead(file)) Assert.Equal(2, input.Length);
            var link = Path.Combine(directory, "link");
            File.CreateSymbolicLink(link, file);
            Assert.Equal(SnapshotNodeKind.Unsafe, SnapshotFileAccess.Kind(link));
            Assert.Throws<IOException>(() => SnapshotFileAccess.OpenRead(link));
            if (!OperatingSystem.IsLinux()) return;
            var fifo = Path.Combine(directory, "fifo");
            var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
            start.ArgumentList.Add(fifo);
            using var create = Process.Start(start)!;
            create.WaitForExit();
            Assert.Equal(0, create.ExitCode);
            Assert.Equal(SnapshotNodeKind.Unsafe, SnapshotFileAccess.Kind(fifo));
            Assert.Throws<IOException>(() => SnapshotFileAccess.OpenRead(fifo));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}