using System.IO.Compression;
using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit per-operation protected-password configuration. No keys or encryption passwords are stored.</summary>
public sealed class PasswordBackupOptions
{
    public bool ProtectPasswords { get; init; } = true;
    /// <summary>Must establish the supported provider/backend; OS detection alone is insufficient.</summary>
    public Func<BraveAppInfo, BraveProfileInfo, PasswordEncryptionProvider>? GetProvider { get; init; }
    public Func<ProcessPresence>? ObserveBrowserState { get; init; }
    /// <summary>Optional owned-key test/host adapter. Native adapters are used when absent.</summary>
    public Func<BraveProfileInfo, ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? Unlock { get; init; }
}

/// <summary>Owns portable captures through archive verification/publication; raw and CSV saving are not supported here.</summary>
internal sealed class PasswordCaptureSession(PasswordBackupOptions options, CancellationToken token) : IDisposable
{
    private readonly List<BravePasswordCapture> _captures = [];
    private long _retainedSourceBytes;
    public long Bytes { get; private set; }
    public int CredentialCount { get; private set; }
    public int NoteCount { get; private set; }
    public int Profiles => _captures.Count;
    public int ExcludedCount { get; private set; }

    public ArchiveCategory Capture(BraveAppInfo installation, BraveProfileInfo profile, string prefix,
        ZipArchive zip, string compression)
    {
        token.ThrowIfCancellationRequested();
        var provider = options.GetProvider!(installation, profile);
        var capture = BravePasswordCapture.Read(profile.ProfileDirectory, provider,
            options.Unlock is null ? null : (state, cancellation) => options.Unlock(profile, state, cancellation),
            options.ObserveBrowserState, token);
        try
        {
            if (capture.RetainedSourceBytes > 128L * 1024 * 1024 - _retainedSourceBytes)
                throw new BackupOperationException("passwords-archive-limit", "Selected password sources exceed the supported 128 MiB operation retention limit.");
            _captures.Add(capture);
            _retainedSourceBytes += capture.RetainedSourceBytes;
        }
        catch { capture.Dispose(); throw; }
        using var document = BravePortablePasswords.Create(capture, token);
        using var payload = PasswordPayloadFormat.Encode(document, token);
        Bytes += payload.Bytes.Length;
        if (Bytes > 128L * 1024 * 1024)
            throw new BackupOperationException("passwords-archive-limit", "Selected password payloads exceed the supported 128 MiB operation limit.");
        var path = prefix + "portable-v1.bin";
        var entry = zip.CreateEntry(path, BackupArchive.Compression(compression));
        using (var output = entry.Open())
        {
            var remaining = payload.Bytes;
            while (!remaining.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(65536, remaining.Length);
                output.Write(remaining.Span[..count]); remaining = remaining[count..];
            }
        }
        foreach (var store in document.Stores)
        {
            CredentialCount += store.Logins.Count;
            ExcludedCount += store.BlockedEntries + store.FederatedEntries;
            foreach (var login in store.Logins) NoteCount += login.Notes.Count;
        }
        return new ArchiveCategory("passwords", 1,
            [new ArchiveFile(path, "portable-passwords", payload.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(payload.Bytes.Span)))]);
    }
    public void VerifySources() { foreach (var capture in _captures) { token.ThrowIfCancellationRequested(); capture.VerifyUnchanged(); } }
    public void EnsureStopped()
    {
        token.ThrowIfCancellationRequested();
        ProcessPresence state;
        try { state = options.ObserveBrowserState?.Invoke() ?? ProcessPresenceProbe.Observe(["brave"], token); }
        catch (OperationCanceledException) { throw; }
        catch { state = ProcessPresence.Unknown; }
        if (state != ProcessPresence.Stopped)
            throw new BackupOperationException("passwords-close-brave", "Close Brave and retry; password sources must remain stopped until publication.");
    }
    public void Dispose() { foreach (var capture in _captures) capture.Dispose(); _captures.Clear(); }
}
