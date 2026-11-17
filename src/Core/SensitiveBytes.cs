using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Owns a serialized sensitive binary payload; no encryption or save authorization is implied.</summary>
public sealed class SensitiveBytes : IDisposable
{
    private byte[]? _bytes;
    internal SensitiveBytes(byte[] ownedBytes) => _bytes = ownedBytes;
    /// <summary>Borrowed bytes are cleared on disposal. Consumers must clear any copies they create.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes is { } bytes ? bytes : throw new ObjectDisposedException(nameof(SensitiveBytes));
    /// <summary>Clears the owned array. Do not use borrowed memory concurrently with disposal.</summary>
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
    public override string ToString() => "Sensitive binary payload (contents hidden)";
}
