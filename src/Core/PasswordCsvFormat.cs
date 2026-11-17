using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Explicit target-platform CSV record separators. Embedded field newlines are preserved.</summary>
public enum PasswordCsvLineEnding { LinuxLf = 0, WindowsCrLf = 1 }

/// <summary>One fully projected CSV row. Secret owners remain owned by the caller and are borrowed during formatting.</summary>
/// <remarks>Does not perform browser credential grouping, domain affiliation, name derivation or note combination.</remarks>
public sealed class PasswordCsvRow(string name, string url, string? username, SensitiveUtf8 password, SensitiveUtf8 note)
{
    public string Name { get; } = name;
    public string Url { get; } = url;
    public string? Username { get; } = username;
    public SensitiveUtf8 Password { get; } = password;
    public SensitiveUtf8 Note { get; } = note;
    public override string ToString() => "Password CSV row (contents hidden)";
}

/// <summary>Bounded Chromium-compatible CSV syntax for already-projected rows. Performs no local save.</summary>
public static class PasswordCsvFormat
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const int MaximumTextBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Sorts by all five UTF-8 columns and returns owned plaintext CSV bytes without a BOM.</summary>
    /// <remarks>
    /// No plaintext password/note strings are created. Input owners must remain alive and unchanged throughout.
    /// Calling this method does not provide save consent. Local CSV saves require a fresh warning/confirmation.
    /// </remarks>
    public static SensitiveBytes Encode(IReadOnlyList<PasswordCsvRow> rows, PasswordCsvLineEnding lineEnding,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        token.ThrowIfCancellationRequested();
        if (rows.Count > 100_000 || lineEnding is not (PasswordCsvLineEnding.LinuxLf or PasswordCsvLineEnding.WindowsCrLf)) throw Invalid();
        var metadata = new List<byte[]>();
        var prepared = new List<ReadOnlyMemory<byte>[]>();
        byte[]? output = null;
        try
        {
            var ending = lineEnding == PasswordCsvLineEnding.WindowsCrLf ? "\r\n"u8 : "\n"u8;
            ReadOnlySpan<byte> header = "name,url,username,password,note"u8;
            long length = header.Length + ending.Length;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row is null || row.Name is null || row.Url is null || row.Password is null || row.Note is null) throw Invalid();
                ReadOnlyMemory<byte>[] fields = [Text(row.Name), Text(row.Url), Text(row.Username ?? ""), row.Password.Utf8, row.Note.Utf8];
                foreach (var field in fields)
                {
                    token.ThrowIfCancellationRequested();
                    length += FieldLength(field.Span);
                    if (length > MaximumBytes) throw Invalid();
                }
                length += 4 + ending.Length;
                if (length > MaximumBytes) throw Invalid();
                prepared.Add(fields);
            }
            // Bounded sort compares UTF-8 bytes, matching std::string ordering rather than UTF-16 string ordering.
            token.ThrowIfCancellationRequested();
            prepared.Sort(Compare);
            token.ThrowIfCancellationRequested();
            output = new byte[(int)length];
            var offset = 0;
            header.CopyTo(output); offset += header.Length;
            ending.CopyTo(output.AsSpan(offset)); offset += ending.Length;
            foreach (var fields in prepared)
            {
                token.ThrowIfCancellationRequested();
                for (var column = 0; column < fields.Length; column++)
                {
                    if (column != 0) output[offset++] = (byte)',';
                    WriteField(fields[column].Span, output, ref offset, token);
                }
                ending.CopyTo(output.AsSpan(offset)); offset += ending.Length;
            }
            token.ThrowIfCancellationRequested();
            var owned = new SensitiveBytes(output);
            output = null;
            return owned;
        }
        finally
        {
            if (output is not null) CryptographicOperations.ZeroMemory(output);
            foreach (var bytes in metadata) CryptographicOperations.ZeroMemory(bytes);
        }

        byte[] Text(string value)
        {
            try
            {
                if (Utf8.GetByteCount(value) > MaximumTextBytes) throw Invalid();
                var bytes = Utf8.GetBytes(value); metadata.Add(bytes); return bytes;
            }
            catch (EncoderFallbackException) { throw Invalid(); }
        }
    }

    private static int Compare(ReadOnlyMemory<byte>[] first, ReadOnlyMemory<byte>[] second)
    {
        for (var column = 0; column < 5; column++)
        {
            var result = first[column].Span.SequenceCompareTo(second[column].Span);
            if (result != 0) return result;
        }
        return 0;
    }

    private static int FieldLength(ReadOnlySpan<byte> value)
    {
        var quote = false;
        var quotes = 0;
        foreach (var item in value)
        {
            if (item is (byte)',' or (byte)'"' or (byte)'\r' or (byte)'\n') quote = true;
            if (item == (byte)'"') quotes++;
        }
        return value.Length + quotes + (quote ? 2 : 0);
    }

    private static void WriteField(ReadOnlySpan<byte> value, byte[] output, ref int offset, CancellationToken token)
    {
        var quote = value.IndexOfAny(",\"\r\n"u8) >= 0;
        if (quote) output[offset++] = (byte)'"';
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            var item = value[index];
            if (item == (byte)'"') output[offset++] = (byte)'"';
            output[offset++] = item;
        }
        if (quote) output[offset++] = (byte)'"';
    }
    private static InvalidDataException Invalid() => new("Password CSV input is invalid, unsupported or over-limit.");
}
