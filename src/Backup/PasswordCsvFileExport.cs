using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Fresh destination-specific plaintext-save warning; contains no credential values.</summary>
public sealed class PasswordCsvExportWarning
{
    public string Destination { get; }
    public bool ReplacesExistingFile { get; }
    public string Message => "Saved passwords will be readable by anyone with file access. " +
        "This export saves a plaintext CSV file and uses a temporary plaintext file in the same folder.\n\n" +
        Destination + (ReplacesExistingFile ? "\n\nThe existing file will be replaced." : "");
    internal PasswordCsvExportWarning(string destination, bool replacesExistingFile)
    { Destination = destination; ReplacesExistingFile = replacesExistingFile; }
    public override string ToString() => "Plaintext password export warning (credential contents excluded)";
}

/// <summary>Safe completion metadata; no credential values are included.</summary>
public sealed record PasswordCsvExportResult(string Destination, int RowCount, long Bytes);

/// <summary>Explicitly confirmed standalone CSV save for already-projected rows, without UI or key access.</summary>
public static class PasswordCsvFileExport
{
    /// <summary>Warns before any local write, then privately stages, verifies and atomically publishes one CSV file.</summary>
    /// <remarks>
    /// The caller must supply the actual selected-profile source check and custom protected browser roots.
    /// Rows borrow secret owners, which must stay alive/unchanged until completion. Confirmation is never cached.
    /// Browser row projection is a separate requirement; this API does not turn a database dump into a browser export.
    /// </remarks>
    public static async Task<PasswordCsvExportResult> SaveAsync(IReadOnlyList<PasswordCsvRow> rows,
        PasswordCsvLineEnding lineEnding, string destination, bool csvExplicitlyChosen,
        IReadOnlyList<string> protectedDirectories, Action verifySources,
        Func<PasswordCsvExportWarning, CancellationToken, Task<bool>> confirmPlaintextSave,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(protectedDirectories);
        ArgumentNullException.ThrowIfNull(verifySources); ArgumentNullException.ThrowIfNull(confirmPlaintextSave);
        token.ThrowIfCancellationRequested();
        if (!csvExplicitlyChosen)
            throw new BackupOperationException("passwords-csv-not-selected", "Explicitly choose plaintext CSV before saving passwords.");
        var frozenRows = rows.ToArray();
        var roots = protectedDirectories.ToArray();
        var target = ExportDestinationGuard.Validate(destination, roots);
        if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new BackupOperationException("passwords-csv-extension", "Choose a CSV destination filename.");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent))
            throw new BackupOperationException("passwords-csv-folder-missing", "Choose an existing destination folder.");
        var initial = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); verifySources(); return Fingerprint(target, token);
        }, token);
        var warning = new PasswordCsvExportWarning(target, initial is not null);
        if (!await confirmPlaintextSave(warning, token).ConfigureAwait(false))
            throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => Save(), token).ConfigureAwait(false);

        PasswordCsvExportResult Save()
        {
            var temporary = Path.Combine(parent, $".bravebackup-passwords-{Guid.NewGuid():N}.tmp");
            var created = false;
            try
            {
                token.ThrowIfCancellationRequested();
                ExportDestinationGuard.Validate(target, roots);
                if (Fingerprint(target, token) != initial) throw Changed();
                verifySources();
                using var payload = PasswordCsvFormat.Encode(frozenRows, lineEnding, token);
                using (var output = BackupFileOperations.CreatePrivateFile(temporary))
                {
                    created = true;
                    var remaining = payload.Bytes;
                    while (!remaining.IsEmpty)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = Math.Min(65536, remaining.Length);
                        output.Write(remaining.Span[..count]); remaining = remaining[count..];
                    }
                    output.Flush(flushToDisk: true);
                }
                // Hash through the bounded file reader and compare actual written bytes, not just file existence.
                var expected = Convert.ToHexString(SHA256.HashData(payload.Bytes.Span));
                var staged = Fingerprint(temporary, token);
                if (staged is null || !staged.EndsWith(":" + expected, StringComparison.Ordinal))
                    throw new BackupOperationException("passwords-csv-verification-failed", "Password CSV output verification failed; it was not published.");
                verifySources();
                ExportDestinationGuard.Validate(target, roots);
                if (Fingerprint(target, token) != initial) throw Changed();
                token.ThrowIfCancellationRequested();
                var length = payload.Bytes.Length;
                File.Move(temporary, target, overwrite: initial is not null);
                created = false;
                return new PasswordCsvExportResult(target, frozenRows.Length, length);
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new BackupOperationException("passwords-csv-cleanup-failed", "Password export did not complete and its temporary plaintext file could not be removed. Check the destination folder."); }
                }
            }
        }
    }

    private static string? Fingerprint(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = SnapshotFileAccess.Kind(path);
        if (kind == SnapshotNodeKind.Missing) return null;
        if (kind != SnapshotNodeKind.RegularFile)
            throw new BackupOperationException("passwords-csv-destination-unsafe", "Choose an ordinary destination file outside browser data.");
        using var input = SnapshotFileAccess.OpenRead(path);
        if (input.Length > PasswordCsvFormat.MaximumBytes)
            throw new BackupOperationException("passwords-csv-destination-limit", "The existing destination exceeds the supported CSV file limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            int count;
            long total = 0;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                total += count;
                if (total > PasswordCsvFormat.MaximumBytes)
                    throw new BackupOperationException("passwords-csv-destination-limit", "The destination exceeded the supported CSV file limit while being read.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return $"{input.Length}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(hash.GetHashAndReset())}";
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static BackupOperationException Changed() => new("destination-changed",
        "The CSV destination changed. Start again to review its replacement and plaintext-save warning.");
}
