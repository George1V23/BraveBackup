using System.Text;

namespace BraveBackup.Core;

/// <summary>Checks bounded five-column password CSV bytes without materializing credential strings.</summary>
public static class PasswordCsvValidation
{
    /// <summary>Validates strict UTF-8, quoting, uniform LF/CRLF record endings and field/row limits; returns data-row count.</summary>
    /// <remarks>Does not establish save consent or verify browser grouping, sort order or credential authenticity.</remarks>
    public static int Validate(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > PasswordCsvFormat.MaximumBytes || !bytes.StartsWith("name,url,username,password,note"u8)) throw Invalid();
        try { new UTF8Encoding(false, true).GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw Invalid(); }
        var offset = "name,url,username,password,note"u8.Length;
        var crlf = offset < bytes.Length && bytes[offset] == '\r';
        EndRecord(bytes, ref offset, crlf);
        var rows = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++rows > 100_000) throw Invalid();
            for (var column = 0; column < 5; column++)
            {
                var limit = column < 3 ? 1024 * 1024 : 4 * 1024 * 1024;
                var length = 0;
                if (offset < bytes.Length && bytes[offset] == '"')
                {
                    offset++;
                    var closed = false;
                    while (offset < bytes.Length)
                    {
                        if ((offset & 65535) == 0) token.ThrowIfCancellationRequested();
                        var value = bytes[offset++];
                        if (value == '"')
                        {
                            if (offset < bytes.Length && bytes[offset] == '"') offset++;
                            else { closed = true; break; }
                        }
                        if (++length > limit) throw Invalid();
                    }
                    if (!closed) throw Invalid();
                }
                else
                {
                    while (offset < bytes.Length && bytes[offset] is not ((byte)',' or (byte)'\r' or (byte)'\n'))
                    {
                        if ((offset & 65535) == 0) token.ThrowIfCancellationRequested();
                        if (bytes[offset++] == '"' || ++length > limit) throw Invalid();
                    }
                }
                if (column == 4) EndRecord(bytes, ref offset, crlf);
                else if (offset >= bytes.Length || bytes[offset++] != ',') throw Invalid();
            }
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    private static void EndRecord(ReadOnlySpan<byte> bytes, ref int offset, bool crlf)
    {
        if (crlf && (offset >= bytes.Length || bytes[offset++] != '\r')) throw Invalid();
        if (offset >= bytes.Length || bytes[offset++] != '\n') throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid or unsupported password CSV payload.");
}
