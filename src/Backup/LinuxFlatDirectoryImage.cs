using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>A bounded, app-owned flat directory image stored inside the existing encrypted safety snapshot.</summary>
internal static class LinuxFlatDirectoryImage
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal const int MaximumFiles = 4096;
    private const int MaximumNameBytes = 255;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Magic => "BBKLD001"u8;

    internal sealed record Entry(string Name, ReadOnlyMemory<byte> Content, ReadOnlyMemory<byte> Metadata);

    /// <summary>Captures the complete flat directory, including an empty directory, without opening any file for writing.</summary>
    internal static RestoreFileImage Capture(RecoveryDirectoryIdentity directory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var metadata = directory.CaptureMetadata();
        var names = directory.EnumerateChildNames().Take(MaximumFiles + 1).Order(StringComparer.Ordinal).ToArray();
        if (names.Length > MaximumFiles)
            throw new IOException("Restore directory contains too many files.");

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(Magic);
        writer.Write(names.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (var index = 0; index < names.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var name = names[index];
                var nameBytes = Utf8.GetBytes(name);
                ValidateName(name, nameBytes.Length, index == 0 ? null : names[index - 1]);
                using var input = directory.OpenReadChild(name);
                var fileMetadata = LinuxRecoveryMetadata.Capture(input.SafeFileHandle);
                var length = input.Length;
                if (length > MaximumBytes - output.Length - 8 - nameBytes.Length - fileMetadata.Length)
                    throw new IOException("Restore directory image exceeds the supported size.");
                writer.Write(nameBytes.Length);
                writer.Write((int)length);
                writer.Write(nameBytes);
                writer.Write(fileMetadata);

                for (var remaining = length; remaining > 0;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (count == 0) throw new IOException("Restore directory file changed during capture.");
                    writer.Write(buffer, 0, count);
                    remaining -= count;
                }

                directory.VerifyChildIdentity(name, input.SafeFileHandle);
                if (input.ReadByte() != -1 ||
                    !LinuxRecoveryMetadata.Capture(input.SafeFileHandle).AsSpan().SequenceEqual(fileMetadata))
                    throw new IOException("Restore directory file changed during capture.");
            }

            var finalNames = directory.EnumerateChildNames().Take(MaximumFiles + 1).Order(StringComparer.Ordinal);
            if (!finalNames.SequenceEqual(names) || !directory.CaptureMetadata().AsSpan().SequenceEqual(metadata))
                throw new IOException("Restore directory changed during capture.");
            token.ThrowIfCancellationRequested();
            return new RestoreFileImage(true, output.GetBuffer().AsSpan(0, (int)output.Length), metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output.GetBuffer().AsSpan(0, (int)output.Length));
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>Builds a directory image with private files using the captured destination metadata.</summary>
    internal static RestoreFileImage Encode(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files,
        ReadOnlyMemory<byte> directoryMetadata)
    {
        if (files.Count > MaximumFiles) throw new InvalidDataException("Too many restore directory files.");
        var fileMetadata = LinuxRecoveryMetadata.CreatePrivateFileMetadata(directoryMetadata.Span);
        var entries = files.OrderBy(file => file.Key, StringComparer.Ordinal).ToArray();
        long length = 12;
        foreach (var (name, content) in entries)
        {
            var byteCount = Utf8.GetByteCount(name);
            ValidateName(name, byteCount, null);
            length += 8L + byteCount + fileMetadata.Length + content.Length;
            if (length > MaximumBytes) throw new InvalidDataException("Restore directory image exceeds the supported size.");
        }
        var bytes = new byte[(int)length];
        try
        {
            using var buffer = new MemoryStream(bytes, writable: true);
            using var writer = new BinaryWriter(buffer);
            writer.Write(Magic);
            writer.Write(entries.Length);
            foreach (var (name, content) in entries)
            {
                var encoded = Utf8.GetBytes(name);
                writer.Write(encoded.Length);
                writer.Write(content.Length);
                writer.Write(encoded);
                writer.Write(fileMetadata);
                writer.Write(content.Span);
            }
            return new RestoreFileImage(true, bytes, directoryMetadata.Span);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Binds the reviewed directory metadata and complete canonical file image.</summary>
    internal static string Digest(RestoreFileImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(image.Metadata.Bytes.Span);
        hash.AppendData(image.Content.Bytes.Span);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Validates the framing and returns entries borrowing the image's owned buffers.</summary>
    internal static Entry[] Decode(RestoreFileImage image)
    {
        var data = image.Content.Bytes;
        if (!image.Exists || data.Length < 12 || data.Length > MaximumBytes || !data.Span.StartsWith(Magic) ||
            image.Metadata.Bytes.Length != LinuxRecoveryMetadata.MetadataSize || !image.Metadata.Bytes.Span.StartsWith("BBKDM001"u8))
            throw new InvalidDataException("Invalid restore directory image.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.Span[8..]);
        if (count is < 0 or > MaximumFiles) throw new InvalidDataException("Invalid restore directory entry count.");
        var entries = new Entry[count];
        var offset = 12;
        string? previous = null;
        for (var index = 0; index < count; index++)
        {
            if (data.Length - offset < 8) throw new InvalidDataException("Truncated restore directory image.");
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(data.Span[offset..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.Span[(offset + 4)..]);
            offset += 8;
            if (nameBytes is < 1 or > MaximumNameBytes || length < 0 ||
                (long)nameBytes + LinuxRecoveryMetadata.MetadataSize + length > data.Length - offset)
                throw new InvalidDataException("Invalid restore directory entry bounds.");
            string name;
            try { name = Utf8.GetString(data.Span.Slice(offset, nameBytes)); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Invalid restore directory filename encoding."); }
            ValidateName(name, nameBytes, previous);
            offset += nameBytes;
            var metadata = data.Slice(offset, LinuxRecoveryMetadata.MetadataSize);
            if (!metadata.Span.StartsWith("BBKLM002"u8)) throw new InvalidDataException("Invalid restore directory file metadata.");
            offset += LinuxRecoveryMetadata.MetadataSize;
            entries[index] = new Entry(name, data.Slice(offset, length), metadata);
            previous = name;
            offset += length;
        }

        if (offset != data.Length) throw new InvalidDataException("Trailing restore directory image data.");
        return entries;
    }

    /// <summary>Compares both directory metadata and the complete canonical file image.</summary>
    internal static bool Matches(RestoreFileImage first, RestoreFileImage second) =>
        first.Exists == second.Exists &&
        first.Metadata.Bytes.Span.SequenceEqual(second.Metadata.Bytes.Span) &&
        first.Content.Bytes.Span.SequenceEqual(second.Content.Bytes.Span);

    private static void ValidateName(string name, int bytes, string? previous)
    {
        if (bytes is < 1 or > MaximumNameBytes || name.Contains('/') || !ExtensionSnapshotFormat.SafePath(name) ||
            (previous is not null && StringComparer.Ordinal.Compare(previous, name) >= 0))
            throw new InvalidDataException("Unsafe or ambiguous restore directory filename.");
    }
}
