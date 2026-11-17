using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit per-operation protected-password configuration. No keys or encryption passwords are stored.</summary>
public sealed class PasswordBackupOptions
{
    public bool ProtectPasswords { get; init; } = true;
    /// <summary>Optional fresh raw-or-cancel choice on eligible portable failures. Invoked on the archive worker; dispatch UI explicitly.</summary>
    public Func<PasswordRawFallbackWarning, CancellationToken, Task<bool>>? ChooseRawPreservation { get; init; }
    /// <summary>Must establish the supported provider/backend; OS detection alone is insufficient.</summary>
    public Func<BraveAppInfo, BraveProfileInfo, PasswordEncryptionProvider>? GetProvider { get; init; }
    public Func<ProcessPresence>? ObserveBrowserState { get; init; }
    /// <summary>Optional owned-key test/host adapter. Native adapters are used when absent.</summary>
    public Func<BraveProfileInfo, ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? Unlock { get; init; }
}

/// <summary>Owns portable/raw captures through archive verification/publication; raw saving requires explicit choice; CSV is not supported here.</summary>
internal sealed class PasswordCaptureSession(PasswordBackupOptions options, CancellationToken token) : IDisposable
{
    private readonly List<BravePasswordCapture> _captures = [];
    private readonly List<PasswordBackupCapture> _fallbacks = [];
    private long _retainedSourceBytes;
    public long Bytes { get; private set; }
    public int CredentialCount { get; private set; }
    public int NoteCount { get; private set; }
    public int Profiles => _captures.Count + RawProfiles;
    public int RawProfiles { get; private set; }
    public int RawFileCount { get; private set; }
    public int IncompleteRawStores { get; private set; }
    public int ExcludedCount { get; private set; }

    public ArchiveCategory Capture(BraveAppInfo installation, BraveProfileInfo profile, string prefix,
        ZipArchive zip, string compression)
    {
        token.ThrowIfCancellationRequested();
        var provider = options.GetProvider!(installation, profile);
        PasswordBackupCapture? selected = null;
        if (options.ChooseRawPreservation is not null)
        {
            selected = PasswordBackupCapture.ReadAsync(profile.ProfileDirectory, provider, options.ChooseRawPreservation,
                options.Unlock is null ? null : (state, cancellation) => options.Unlock(profile, state, cancellation),
                options.ObserveBrowserState, token).GetAwaiter().GetResult();
            if (selected.Kind == PasswordBackupCaptureKind.RawNative) return CaptureRaw(selected, prefix, zip, compression);
        }
        var capture = selected?.Portable ?? BravePasswordCapture.Read(profile.ProfileDirectory, provider,
            options.Unlock is null ? null : (state, cancellation) => options.Unlock(profile, state, cancellation),
            options.ObserveBrowserState, token);
        try
        {
            if (capture.RetainedSourceBytes > 128L * 1024 * 1024 - _retainedSourceBytes)
                throw new BackupOperationException("passwords-archive-limit", "Selected password sources exceed the supported 128 MiB operation retention limit.");
            _captures.Add(capture);
            if (selected is not null) _fallbacks.Add(selected);
            _retainedSourceBytes += capture.RetainedSourceBytes;
        }
        catch { selected?.Dispose(); capture.Dispose(); throw; }
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
    private ArchiveCategory CaptureRaw(PasswordBackupCapture selected, string prefix, ZipArchive zip, string compression)
    {
        var snapshot = selected.Raw;
        var rawFiles = snapshot.Files;
        var retained = rawFiles.Values.Sum(value => (long)value.Length);
        try
        {
            if (retained > 128L * 1024 * 1024 - _retainedSourceBytes)
                throw new BackupOperationException("passwords-archive-limit", "Selected password sources exceed the supported operation retention limit.");
            _fallbacks.Add(selected); _retainedSourceBytes += retained;
        }
        catch { selected.Dispose(); throw; }
        var files = new List<ArchiveFile>();
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, captureKind = "raw-native-password-files", portableCaptureCompleted = false,
            reasonCode = selected.PortableFailureCode, missingMainDatabases = snapshot.MissingMainDatabases
        });
        try { files.Add(Write(prefix + "raw-index-v1.json", "raw-password-index", index)); }
        finally { CryptographicOperations.ZeroMemory(index); }
        foreach (var file in rawFiles)
        {
            token.ThrowIfCancellationRequested();
            files.Add(Write(prefix + "raw/" + file.Key, "native-password-file", file.Value));
        }
        snapshot.VerifyUnchanged();
        RawProfiles++; RawFileCount += rawFiles.Count; IncompleteRawStores += snapshot.MissingMainDatabases.Count;
        return new ArchiveCategory("passwords", 2, files.ToArray());

        ArchiveFile Write(string path, string role, ReadOnlyMemory<byte> bytes)
        {
            Bytes += bytes.Length;
            if (Bytes > 128L * 1024 * 1024)
                throw new BackupOperationException("passwords-archive-limit", "Selected password payloads exceed the supported operation limit.");
            using var output = zip.CreateEntry(path, BackupArchive.Compression(compression)).Open();
            var remaining = bytes;
            while (!remaining.IsEmpty)
            {
                token.ThrowIfCancellationRequested(); var count = Math.Min(65536, remaining.Length);
                output.Write(remaining.Span[..count]); remaining = remaining[count..];
            }
            return new ArchiveFile(path, role, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
        }
    }
    public void VerifySources()
    {
        foreach (var capture in _captures) { token.ThrowIfCancellationRequested(); capture.VerifyUnchanged(); }
        foreach (var fallback in _fallbacks.Where(value => value.Kind == PasswordBackupCaptureKind.RawNative)) fallback.VerifyUnchanged();
    }
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
    public void Dispose() { foreach (var capture in _captures) capture.Dispose(); foreach (var fallback in _fallbacks) fallback.Dispose(); _captures.Clear(); _fallbacks.Clear(); }
}
