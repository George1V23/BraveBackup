using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit result classification; raw preservation must never be reported as portable password capture.</summary>
public enum PasswordBackupCaptureKind { Portable, RawNative }

/// <summary>Safe failure explanation for a fresh user choice; no credentials or underlying exception text are retained.</summary>
public sealed class PasswordRawFallbackWarning
{
    public string ProfileDirectory { get; }
    public string ReasonCode { get; }
    public string ReasonDescription => ReasonCode switch
    {
        "passwords-key-unlock-failed" or "passwords-key-source-invalid" => "The selected source could not unlock the existing password key.",
        "passwords-encryption-unsupported" or "passwords-key-envelope-unsupported" => "The saved-password protection is unsupported for the selected source.",
        "passwords-schema-unsupported" or "passwords-scheme-unsupported" => "The saved-password data format is unsupported.",
        "passwords-journal-unsupported" => "The password database has a rollback journal that cannot be safely interpreted by this capture path.",
        "passwords-capture-limit" => "The portable password data exceeds the supported capture limit.",
        _ => "The password data is corrupt, unreadable or cannot be decoded completely."
    };
    public string Message => "Portable password capture did not complete. " + ReasonDescription + " Available password-data files can instead be kept unchanged as raw/native data. " +
        "They may be incomplete and are not guaranteed portable or recoverable. OS key stores and Local State will not be included. " +
        "Choose raw preservation or cancel. This choice does not authorize an unencrypted save.";
    internal PasswordRawFallbackWarning(string profileDirectory, string reasonCode)
    { ProfileDirectory = profileDirectory; ReasonCode = reasonCode; }
    public override string ToString() => "Raw password preservation warning (credential contents excluded)";
}

/// <summary>Owns either a portable capture or an explicitly selected raw fallback; performs no archive/CSV save.</summary>
public sealed class PasswordBackupCapture : IDisposable
{
    private readonly BravePasswordCapture? _portable;
    private readonly BraveRawPasswordSnapshot? _raw;
    private bool _disposed;
    public PasswordBackupCaptureKind Kind { get; }
    public string? PortableFailureCode { get; }
    /// <summary>Null means raw data was not decoded; it must not be displayed as zero captured credentials.</summary>
    public int? CredentialCount { get { Check(); return _portable?.Databases.Values.Sum(database => database.Rows.Count); } }
    public BravePasswordCapture Portable { get { Check(); return _portable ?? throw new InvalidOperationException("This result is raw/native preservation, not portable credentials."); } }
    public BraveRawPasswordSnapshot Raw { get { Check(); return _raw ?? throw new InvalidOperationException("This result contains portable credentials, not raw preservation."); } }
    private PasswordBackupCapture(BravePasswordCapture portable) { _portable = portable; Kind = PasswordBackupCaptureKind.Portable; }
    private PasswordBackupCapture(BraveRawPasswordSnapshot raw, string failureCode)
    { _raw = raw; Kind = PasswordBackupCaptureKind.RawNative; PortableFailureCode = failureCode; }

    /// <summary>Attempts portable capture first; eligible failures offer one fresh explicit raw-or-cancel choice.</summary>
    /// <remarks>Native access runs on a worker. The callback resumes on the caller's context when available.</remarks>
    public static async Task<PasswordBackupCapture> ReadAsync(string profile, PasswordEncryptionProvider provider,
        Func<PasswordRawFallbackWarning, CancellationToken, Task<bool>> chooseRawPreservation,
        Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? unlock = null,
        Func<ProcessPresence>? observeBrowserState = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(chooseRawPreservation);
        token.ThrowIfCancellationRequested();
        try
        {
            return new PasswordBackupCapture(await Task.Run(() => BravePasswordCapture.Read(profile, provider,
                unlock, observeBrowserState, token), token));
        }
        catch (BackupOperationException error) when (Eligible(error.Code))
        {
            token.ThrowIfCancellationRequested();
            var warning = new PasswordRawFallbackWarning(Path.GetFullPath(profile), error.Code);
            if (!await chooseRawPreservation(warning, token)) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            var raw = await Task.Run(() => BraveRawPasswordSnapshot.Read(profile, true, observeBrowserState, token), token);
            return new PasswordBackupCapture(raw, error.Code);
        }
    }

    /// <summary>Verifies the chosen source again before a future publication.</summary>
    public void VerifyUnchanged() { Check(); if (_portable is not null) _portable.VerifyUnchanged(); else _raw!.VerifyUnchanged(); }
    /// <summary>Clears all owned raw or decoded buffers. Repeated disposal is safe.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _portable?.Dispose(); _raw?.Dispose(); }
    public override string ToString() => "Password backup capture (credential contents hidden)";
    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static bool Eligible(string code) => code is "passwords-source-invalid" or "passwords-database-invalid" or
        "passwords-schema-unsupported" or "passwords-scheme-unsupported" or "passwords-journal-unsupported" or
        "passwords-encryption-unsupported" or "passwords-decode-failed" or "passwords-key-unlock-failed" or
        "passwords-key-envelope-unsupported" or "passwords-key-source-invalid" or "passwords-capture-limit";
}
