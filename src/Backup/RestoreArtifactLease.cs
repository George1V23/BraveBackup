using BraveBackup.Platform;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Linux operation-lifetime recovery ownership. Does not modify profiles or implement a target writer.</summary>
public sealed class RestoreArtifactLease : IDisposable
{
    private readonly RestoreArtifactScope _scope;
    private readonly RecoveryDirectoryIdentity _directory;
    private readonly object _gate = new();
    private bool _disposed;
    private string? _journalHash;
    private string? _restartHash;
    private RestoreArtifactLease(RestoreArtifactScope scope, RecoveryDirectoryIdentity directory)
    { _scope = scope; _directory = directory; }
    /// <summary>Acquires ownership without creating artifacts; unsupported hosts fail closed.</summary>
    public static RestoreArtifactLease Acquire(RestoreArtifactScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.Validate();
        var directory = RecoveryDirectoryIdentity.Open(Path.GetDirectoryName(scope.SafetyPath)!);
        try { directory.AcquireExclusive(); scope.Validate(); return new(scope, directory); }
        catch { directory.Dispose(); throw; }
    }
    /// <summary>Publishes original safety state once while retaining ownership across subsequent pauses.</summary>
    public void SaveSafety(ReadOnlyMemory<byte> encrypted)
    {
        lock (_gate)
        {
            Verify();
            CheckFraming(encrypted);
            _directory.PublishChild(Path.GetFileName(_scope.SafetyPath), encrypted, false);
            _directory.Flush();
        }
    }
    /// <summary>Publishes the immutable restart record without replacing any existing file.</summary>
    public void SaveRestartRecord(ReadOnlyMemory<byte> encrypted)
    {
        lock (_gate)
        {
            Verify();
            CheckFraming(encrypted);
            _directory.PublishChild(Path.GetFileName(_scope.RestartRecordPath), encrypted, false);
            _directory.Flush();
            _restartHash = Convert.ToHexStringLower(SHA256.HashData(encrypted.Span));
        }
    }

    /// <summary>Reads the published restart record for authenticated coordinator verification.</summary>
    public byte[] ReadRestartRecord() { lock (_gate) { Verify(); return Read(Path.GetFileName(_scope.RestartRecordPath)); } }

    internal void AdoptVerifiedArtifacts(ReadOnlySpan<byte> record, ReadOnlySpan<byte> journal)
    {
        lock (_gate)
        {
            Verify();
            if (_restartHash is not null || _journalHash is not null)
                throw new InvalidOperationException("Recovery artifacts are already owned.");
            _restartHash = Convert.ToHexStringLower(SHA256.HashData(record));
            _journalHash = Convert.ToHexStringLower(SHA256.HashData(journal));
            VerifyRestartRecord();
            VerifyJournal();
        }
    }

    internal void VerifyJournal()
    {
        lock (_gate)
        {
            Verify();
            var previous = Read(Path.GetFileName(_scope.JournalPath));
            try
            {
                if (Convert.ToHexStringLower(SHA256.HashData(previous)) != _journalHash)
                    throw new IOException("Recovery journal changed outside its operation.");
            }
            finally { CryptographicOperations.ZeroMemory(previous); }
        }
    }

    /// <summary>Publishes a journal revision under the same operation-lifetime lock.</summary>
    public void SaveJournal(ReadOnlyMemory<byte> encrypted)
    {
        lock (_gate)
        {
            Verify(); CheckFraming(encrypted);
            if (_restartHash is not null) VerifyRestartRecord();
            if (_journalHash is not null) VerifyJournal();
            _directory.PublishChild(Path.GetFileName(_scope.JournalPath), encrypted, _journalHash is not null);
            _journalHash = Convert.ToHexStringLower(SHA256.HashData(encrypted.Span));
            _directory.Flush();
        }
    }
    /// <summary>Reads the owned safety artifact for coordinator authentication.</summary>
    public byte[] ReadSafety() { lock (_gate) { Verify(); return Read(Path.GetFileName(_scope.SafetyPath)); } }
    /// <summary>Reads the owned journal artifact for coordinator authentication.</summary>
    public byte[] ReadJournal() { lock (_gate) { Verify(); return Read(Path.GetFileName(_scope.JournalPath)); } }
    /// <summary>Checks the already authenticated record's ciphertext at a profile publication boundary.</summary>
    internal void VerifyRestartRecord()
    {
        lock (_gate)
        {
            Verify();
            if (_restartHash is null) throw new IOException("Verified restart record is missing.");
            var record = Read(Path.GetFileName(_scope.RestartRecordPath));
            try
            {
                if (Convert.ToHexStringLower(SHA256.HashData(record)) != _restartHash)
                    throw new IOException("Restart record changed outside its operation.");
            }
            finally { CryptographicOperations.ZeroMemory(record); }
        }
    }

    /// <summary>Revalidates scope and retained directory identity without creating files.</summary>
    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _scope.Validate(); _directory.Verify();
    }
    private byte[] Read(string name)
    {
        using var input = _directory.OpenReadChild(name);
        if (input.Length is < ArchiveEncryption.HeaderSize or > RestoreArtifactStorage.MaximumBytes || !ArchiveEncryption.IsEncrypted(input))
            throw new InvalidDataException("Invalid recovery artifact framing or size.");
        var bytes = new byte[(int)input.Length];
        try
        {
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("Recovery artifact changed length.");
            Verify(); return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static void CheckFraming(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < ArchiveEncryption.HeaderSize || bytes.Length > RestoreArtifactStorage.MaximumBytes ||
            !bytes.Span[..8].SequenceEqual("BBKENC1\n"u8)) throw new InvalidDataException("An encrypted recovery artifact is required.");
    }
    /// <summary>Releases ownership without deleting recovery artifacts.</summary>
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _directory.Dispose(); } }
}
