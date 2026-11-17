using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Researched canonical-note projection for an already-established browser credential group.</summary>
public static class BravePasswordCsvNotes
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Combines distinct nonempty canonical notes using UTF-16 ordinal order and LF, without plaintext strings.</summary>
    /// <remarks>Grouping must be established by the caller. Nonempty named notes fail rather than being silently omitted.</remarks>
    public static SensitiveUtf8 Combine(IReadOnlyList<PortablePasswordLogin> groupedLogins,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(groupedLogins);
        token.ThrowIfCancellationRequested();
        if (groupedLogins.Count > 100_000) throw Invalid();
        var characters = new List<char[]>();
        byte[]? combined = null;
        try
        {
            long sourceBytes = 0;
            var noteCount = 0;
            foreach (var login in groupedLogins)
            {
                token.ThrowIfCancellationRequested();
                if (login is null) throw Invalid();
                var canonicalSeen = false;
                foreach (var note in login.Notes)
                {
                    token.ThrowIfCancellationRequested();
                    if (note is null || note.Name is null || ++noteCount > 100_000) throw Invalid();
                    var bytes = note.Value.Utf8;
                    sourceBytes += bytes.Length;
                    if (sourceBytes > 32L * 1024 * 1024) throw Invalid();
                    if (note.Name.Length != 0)
                    {
                        if (!bytes.IsEmpty)
                            throw new BackupOperationException("passwords-csv-notes-unsupported",
                                "A nonempty named password note cannot be represented by the researched browser CSV note rule. Use a protected portable backup to preserve all notes.");
                        continue;
                    }
                    if (canonicalSeen) throw Invalid();
                    canonicalSeen = true;
                    if (!bytes.IsEmpty)
                    {
                        var value = new char[Utf8.GetCharCount(bytes.Span)];
                        try { _ = Utf8.GetChars(bytes.Span, value); characters.Add(value); }
                        catch { Array.Clear(value); throw; }
                    }
                }
            }
            // Chromium stores this set as std::u16string: UTF-8 ordering would differ for supplementary characters.
            characters.Sort((first, second) => first.AsSpan().SequenceCompareTo(second));
            token.ThrowIfCancellationRequested();
            var distinct = new List<char[]>();
            long length = 0;
            foreach (var value in characters)
            {
                token.ThrowIfCancellationRequested();
                if (distinct.Count != 0 && distinct[^1].AsSpan().SequenceEqual(value)) continue;
                length += Utf8.GetByteCount(value) + (distinct.Count == 0 ? 0 : 1);
                if (length > SensitiveUtf8.MaximumBytes) throw Invalid();
                distinct.Add(value);
            }
            combined = new byte[(int)length];
            var offset = 0;
            foreach (var value in distinct)
            {
                token.ThrowIfCancellationRequested();
                if (offset != 0) combined[offset++] = (byte)'\n';
                offset += Utf8.GetBytes(value, combined.AsSpan(offset));
            }
            token.ThrowIfCancellationRequested();
            return new SensitiveUtf8(combined);
        }
        finally
        {
            foreach (var value in characters) Array.Clear(value);
            if (combined is not null) CryptographicOperations.ZeroMemory(combined);
        }
    }
    private static BackupOperationException Invalid() => new("passwords-csv-note-projection-invalid",
        "Password note projection is invalid or exceeds its supported limits; no note was silently omitted.");
}
