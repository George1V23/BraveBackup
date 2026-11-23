using System.Buffers;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Retains an existing flat target directory and publishes complete images through atomic directory exchange.</summary>
internal sealed class LinuxRestoreDirectoryCheckpoint : ILinuxRestoreTarget
{
    private readonly RecoveryDirectoryIdentity _parent;
    private readonly string _parentPath;
    private readonly string _name;
    private RecoveryDirectoryIdentity _identity;
    private RestoreFileImage _expected;

    internal LinuxRestoreDirectoryCheckpoint(string parentPath, string name, RestoreFileImage replacement, CancellationToken token,
        string? requiredOriginalDigest = null)
    {
        LinuxFlatDirectoryImage.Decode(replacement);
        _parentPath = Path.GetFullPath(parentPath);
        _name = name;
        _parent = RecoveryDirectoryIdentity.Open(_parentPath);
        try
        {
            _identity = _parent.OpenDirectoryChild(name);
            try
            {
                _expected = LinuxFlatDirectoryImage.Capture(_identity, token);
                if (requiredOriginalDigest is not null && LinuxFlatDirectoryImage.Digest(_expected) != requiredOriginalDigest)
                {
                    _expected.Dispose();
                    throw new BackupOperationException("restore-extension-target", "The destination Local store changed after Recheck.");
                }
            }
            catch { _identity.Dispose(); throw; }
        }
        catch { _parent.Dispose(); throw; }
    }

    public string CaptureRestartDirectoryEvidence() => _parent.CaptureRestartEvidence();

    public long ImageBytes => _expected.Content.Bytes.Length + _expected.Metadata.Bytes.Length;

    /// <summary>Reads the current directory only if its identity, metadata, names and contents still match.</summary>
    public RestoreFileImage ReadCurrent(CancellationToken token)
    {
        _parent.VerifyDirectoryChild(_name, _identity);
        using var directory = _parent.OpenDirectoryChild(_name);
        if (!directory.MatchesRetainedIdentity(_identity))
            throw new IOException("The restore directory changed identity.");
        var actual = LinuxFlatDirectoryImage.Capture(directory, token);
        try
        {
            _parent.VerifyDirectoryChild(_name, _identity);
            if (!LinuxFlatDirectoryImage.Matches(actual, _expected))
                throw new IOException("The restore directory changed after its state was captured.");
            return actual;
        }
        catch { actual.Dispose(); throw; }
    }

    /// <summary>Publishes one verified image after the coordinator has saved safety data and authorized the write.</summary>
    public void Publish(RestoreFileImage replacement, Action verifyContext)
    {
        var entries = LinuxFlatDirectoryImage.Decode(replacement);
        using var writer = RecoveryDirectoryIdentity.Open(_parentPath);
        writer.AcquireExclusive();
        if (!writer.RefersToSameDirectory(_parent))
            throw new IOException("The restore directory parent changed.");

        using (ReadCurrent(CancellationToken.None)) { }
        var stageName = ".restore-directory-" + Guid.NewGuid().ToString("N");
        using var stage = writer.CreatePrivateDirectory(stageName);
        const string imageName = "image";
        RecoveryDirectoryIdentity? staged = null;
        RestoreFileImage? next = null;
        var stagedEntries = new List<LinuxFlatDirectoryImage.Entry>(entries.Length);
        var exchanged = false;
        try
        {
            next = replacement.Clone();
            staged = stage.CreatePrivateDirectory(imageName);
            foreach (var entry in entries)
            {
                staged.PublishNewRecoveryFile(entry.Name, entry.Content, entry.Metadata);
                stagedEntries.Add(entry);
            }
            staged.ApplyMetadata(replacement.Metadata.Bytes.Span);
            VerifyImage(staged, replacement, "Staged restore directory verification failed.");
            staged.Flush();
            stage.Flush();
            writer.Flush();

            verifyContext();
            VerifyImage(staged, replacement, "Staged restore directory changed before publication.");
            using (ReadCurrent(CancellationToken.None)) { }
            // From this syscall onward the coordinator must be able to recover the newly published image.
            writer.ExchangeDirectoryChild(_name, _identity, stage, imageName, staged);
            exchanged = true;
            using var oldIdentity = _identity;
            using var previous = _expected;
            _identity = staged;
            _expected = next;
            staged = null;
            next = null;

            writer.Flush();
            stage.Flush();
            using (ReadCurrent(CancellationToken.None)) { }
            stage.VerifyDirectoryChild(imageName, oldIdentity);
            using var displaced = stage.OpenDirectoryChild(imageName);
            VerifyImage(displaced, previous, "The displaced restore directory changed. Private staging was retained.");
            RemoveFlatChild(stage, imageName, displaced, LinuxFlatDirectoryImage.Decode(previous));
            writer.RemoveEmptyDirectory(stageName, stage);
            writer.Flush();
        }
        finally
        {
            try
            {
                if (!exchanged)
                {
                    if (staged is not null)
                        RemoveFlatChild(stage, imageName, staged, stagedEntries);
                    writer.RemoveEmptyDirectory(stageName, stage);
                    writer.Flush();
                }
            }
            finally
            {
                staged?.Dispose();
                next?.Dispose();
            }
        }
    }

    private static void VerifyImage(RecoveryDirectoryIdentity directory, RestoreFileImage expected, string failure)
    {
        using var actual = LinuxFlatDirectoryImage.Capture(directory);
        if (!LinuxFlatDirectoryImage.Matches(actual, expected)) throw new IOException(failure);
    }

    private static void RemoveFlatChild(RecoveryDirectoryIdentity parent, string name,
        RecoveryDirectoryIdentity child, IReadOnlyList<LinuxFlatDirectoryImage.Entry> files)
    {
        child.AcquireExclusive();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            foreach (var entry in files)
            {
                using var handle = child.OpenReadChild(entry.Name);
                var remaining = entry.Content.Span;
                while (!remaining.IsEmpty)
                {
                    var count = handle.Read(buffer, 0, Math.Min(buffer.Length, remaining.Length));
                    if (count == 0 || !remaining[..count].SequenceEqual(buffer.AsSpan(0, count)))
                        throw new IOException("Recovery staging file changed and was retained.");
                    remaining = remaining[count..];
                }
                if (handle.ReadByte() != -1 ||
                    !LinuxRecoveryMetadata.Capture(handle.SafeFileHandle).AsSpan().SequenceEqual(entry.Metadata.Span))
                    throw new IOException("Recovery staging file changed and was retained.");
                child.DeleteRecoveryFile(entry.Name, handle.SafeFileHandle, () => parent.VerifyDirectoryChild(name, child));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

        child.Flush();
        parent.RemoveEmptyDirectory(name, child);
        parent.Flush();
    }

    public void Dispose()
    {
        _expected.Dispose();
        _identity.Dispose();
        _parent.Dispose();
    }
}
