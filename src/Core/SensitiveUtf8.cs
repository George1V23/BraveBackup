using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>
/// Owns a bounded validated UTF-8 secret without creating a managed plaintext string.
/// Borrowed memory remains backed by the owned array and is cleared when disposed.
/// Callers must not retain copies or use it concurrently with disposal.
/// </summary>
public sealed class SensitiveUtf8 : IDisposable
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private byte[]? _bytes;

    /// <summary>Copies valid UTF-8 into owned memory; malformed data produces a content-free error.</summary>
    public SensitiveUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaximumBytes) throw new InvalidDataException("Sensitive value exceeds its byte limit.");
        try { _ = new UTF8Encoding(false, true).GetCharCount(utf8); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Sensitive value is not valid UTF-8."); }
        _bytes = utf8.ToArray();
    }

    /// <summary>Gets borrowed read-only bytes for a trusted consumer; this does not authorize a plaintext save.</summary>
    public ReadOnlyMemory<byte> Utf8 => _bytes is { } bytes ? bytes : throw new ObjectDisposedException(nameof(SensitiveUtf8));

    /// <summary>Clears the owned array and prevents further access through this owner.</summary>
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    public override string ToString() => "Sensitive UTF-8 value (contents hidden)";
}
