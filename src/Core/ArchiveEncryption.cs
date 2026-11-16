using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Implements the documented BBKENC1 envelope without staging plaintext on disk.</summary>
public static class ArchiveEncryption
{
    public const int ChunkSize = 1024 * 1024;
    public const int HeaderSize = 48;
    public const int Iterations = 600_000;
    private static ReadOnlySpan<byte> Magic => "BBKENC1\n"u8;

    /// <summary>Detects encrypted framing on a seekable stream, retaining its position.</summary>
    public static bool IsEncrypted(Stream source)
    {
        var position = source.Position;
        Span<byte> bytes = stackalloc byte[8];
        var read = source.Read(bytes);
        source.Position = position;
        return read == 8 && bytes.SequenceEqual(Magic);
    }

    /// <summary>Creates an encrypting output stream; disposal finalizes the authenticated terminal record.</summary>
    public static Stream CreateWriter(Stream target, string password, CancellationToken cancellationToken = default) =>
        new EncryptingStream(target, password, cancellationToken);

    /// <summary>Authenticates the complete envelope, then exposes bounded-memory random access for ZIP verification.</summary>
    /// <remarks>The caller owns source. No decrypted temporary file is created.</remarks>
    public static Stream OpenReader(Stream source, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return new DecryptingStream(source, password, cancellationToken); }
        catch (CryptographicException)
        { throw new CryptographicException("Password is incorrect or the backup is damaged."); }
    }

    // Only the friend test assembly can supply deterministic material for interoperability vectors.
    internal static Stream CreateVectorWriter(Stream target, string password, byte[] header) =>
        new EncryptingStream(target, password, CancellationToken.None, header);

    private static byte[] NewHeader()
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        header[10] = header[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), Iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), ChunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(20, 24));
        return header;
    }

    private static void ValidateHeader(byte[] header)
    {
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != 1 || header[10] != 1 || header[11] != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != Iterations ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != ChunkSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44)) != 0)
            throw new InvalidDataException("Unsupported encrypted archive header.");
    }

    private static AesGcm DeriveCipher(string password, byte[] header)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("An encryption password is required.");
        var bytes = Encoding.UTF8.GetBytes(password);
        var key = new byte[32];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(bytes, header.AsSpan(20, 16), key, Iterations, HashAlgorithmName.SHA256);
            return new AesGcm(key, 16);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(key); }
    }

    private static void Frame(byte[] header, uint index, int length, Span<byte> nonce, Span<byte> aad)
    {
        header.AsSpan(36, 8).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], index);
        header.CopyTo(aad);
        BinaryPrimitives.WriteUInt32LittleEndian(aad[48..], index);
        BinaryPrimitives.WriteInt32LittleEndian(aad[52..], length);
    }

    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _target;
        private readonly CancellationToken _token;
        private readonly byte[] _header;
        private readonly byte[] _buffer = new byte[ChunkSize];
        private readonly byte[] _ciphertext = new byte[ChunkSize];
        private readonly AesGcm _cipher;
        private int _count;
        private uint _index;
        private long _length;
        private bool _disposed;

        public EncryptingStream(Stream target, string password, CancellationToken token, byte[]? testHeader = null)
        {
            _target = target; _token = token;
            _header = testHeader?.ToArray() ?? NewHeader();
            ValidateHeader(_header);
            _cipher = DeriveCipher(password, _header);
            try { target.Write(_header); }
            catch { _cipher.Dispose(); throw; }
        }

        public override void Write(ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            if (source.Length > ArchiveLimits.MaxBytes - _length) throw new InvalidDataException("Archive exceeds 100 GiB.");
            _length += source.Length;
            while (!source.IsEmpty)
            {
                var take = Math.Min(source.Length, ChunkSize - _count);
                source[..take].CopyTo(_buffer.AsSpan(_count));
                source = source[take..]; _count += take;
                if (_count == ChunkSize) WriteRecord();
            }
        }

        private void WriteRecord()
        {
            _token.ThrowIfCancellationRequested();
            if (_index == uint.MaxValue) throw new InvalidDataException("Encryption record limit exceeded.");
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[56];
            Span<byte> tag = stackalloc byte[16];
            Frame(_header, _index++, _count, nonce, aad);
            _cipher.Encrypt(nonce, _buffer.AsSpan(0, _count), _ciphertext.AsSpan(0, _count), tag, aad);
            _target.Write(aad[52..]);
            _target.Write(_ciphertext.AsSpan(0, _count));
            _target.Write(tag);
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _count));
            _count = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            try
            {
                if (disposing && !_token.IsCancellationRequested)
                {
                    if (_count != 0) WriteRecord();
                    WriteRecord(); // Mandatory authenticated terminal record.
                }
            }
            finally
            {
                _disposed = true; _cipher.Dispose();
                CryptographicOperations.ZeroMemory(_buffer);
                CryptographicOperations.ZeroMemory(_ciphertext);
                base.Dispose(disposing);
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => _target.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly CancellationToken _token;
        private readonly byte[] _header = new byte[HeaderSize];
        private readonly byte[] _plain = new byte[ChunkSize];
        private readonly byte[] _encrypted = new byte[ChunkSize];
        private readonly AesGcm _cipher;
        private long _length;
        private long _position;
        private long _cachedIndex = -1;
        private int _cachedLength;
        private bool _disposed;

        public DecryptingStream(Stream source, string password, CancellationToken token)
        {
            _source = source; _token = token;
            source.Position = 0;
            source.ReadExactly(_header);
            ValidateHeader(_header);
            _cipher = DeriveCipher(password, _header);
            try
            {
                uint index = 0;
                var shortRecord = false;
                while (true)
                {
                    var size = ReadRecord(index++);
                    if (size == 0)
                    {
                        if (source.Position != source.Length) throw new InvalidDataException("Trailing encrypted data.");
                        break;
                    }
                    if (shortRecord) throw new InvalidDataException("Noncanonical encryption records.");
                    shortRecord = size < ChunkSize;
                    _length += size;
                    if (_length > ArchiveLimits.MaxBytes) throw new InvalidDataException("Archive exceeds 100 GiB.");
                }
                CryptographicOperations.ZeroMemory(_plain);
            }
            catch
            {
                _cipher.Dispose();
                CryptographicOperations.ZeroMemory(_plain);
                CryptographicOperations.ZeroMemory(_encrypted);
                throw;
            }
        }

        private int ReadRecord(uint index)
        {
            _token.ThrowIfCancellationRequested();
            Span<byte> size = stackalloc byte[4];
            _source.ReadExactly(size);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(size);
            if (length > ChunkSize) throw new InvalidDataException("Invalid encrypted record length.");
            _source.ReadExactly(_encrypted.AsSpan(0, (int)length));
            Span<byte> tag = stackalloc byte[16];
            _source.ReadExactly(tag);
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[56];
            Frame(_header, index, (int)length, nonce, aad);
            _cipher.Decrypt(nonce, _encrypted.AsSpan(0, (int)length), tag, _plain.AsSpan(0, (int)length), aad);
            return (int)length;
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            var total = 0;
            while (!buffer.IsEmpty && _position < _length)
            {
                var index = _position / ChunkSize;
                if (_cachedIndex != index)
                {
                    _source.Position = HeaderSize + index * (ChunkSize + 20L);
                    _cachedLength = ReadRecord(checked((uint)index));
                    _cachedIndex = index;
                }
                var offset = (int)(_position % ChunkSize);
                var take = Math.Min(buffer.Length, _cachedLength - offset);
                _plain.AsSpan(offset, take).CopyTo(buffer);
                _position += take; total += take; buffer = buffer[take..];
            }
            return total;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position,
                SeekOrigin.End => _length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            if (next < 0) throw new IOException("Cannot seek before the archive.");
            return _position = next;
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true; _cipher.Dispose();
                CryptographicOperations.ZeroMemory(_plain); CryptographicOperations.ZeroMemory(_encrypted);
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override long Length => _length;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
