using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace BraveBackup.Platform;

/// <summary>
/// Owns a native SQLite database backed by a private memory copy, never a source
/// filename. Callers must dispose all commands/readers before disposing this owner.
/// </summary>
public sealed class SqliteMemoryImage : IDisposable
{
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private static readonly byte[] Zeros = new byte[65536];
    private readonly SqliteConnection _connection;
    private readonly Progress _progress;
    private readonly int _length;
    private readonly bool _writable;
    private IntPtr _buffer;
    private long _steps;

    /// <summary>Gets the trusted caller's connection for fixed queries; no source path is ever passed to SQLite.</summary>
    public SqliteConnection Connection => _buffer != IntPtr.Zero ? _connection : throw new ObjectDisposedException(nameof(SqliteMemoryImage));

    /// <summary>Copies a bounded assembled image into native memory and configures read-only, bounded execution.</summary>
    public SqliteMemoryImage(byte[] image, CancellationToken token = default)
        : this(image, false, token)
    {
    }

    /// <summary>Opens a separate bounded, writable copy that can be serialized after staging changes.</summary>
    public static SqliteMemoryImage OpenWritableCopy(byte[] image, CancellationToken token = default) =>
        new(image, true, token);

    private SqliteMemoryImage(byte[] image, bool writable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (image.Length is < 512 or > MaximumImageBytes) throw new InvalidDataException("Invalid memory image size.");
        _writable = writable;
        var capacity = writable ? MaximumImageBytes : image.Length;
        _length = capacity + 32; // SQLite documents padding for malformed input.
        _connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _progress = _ => token.IsCancellationRequested || Interlocked.Increment(ref _steps) > 100_000 ? 1 : 0;
        try
        {
            _buffer = Marshal.AllocHGlobal(_length);
            Marshal.Copy(image, 0, _buffer, image.Length);
            for (var offset = image.Length; offset < _length; offset += Zeros.Length)
                Marshal.Copy(Zeros, 0, IntPtr.Add(_buffer, offset), Math.Min(Zeros.Length, _length - offset));
            // SQLite explicitly documents this normalization for a deserialized WAL-mode image.
            Marshal.WriteByte(_buffer, 18, 1); Marshal.WriteByte(_buffer, 19, 1);
            _connection.Open();
            var handle = _connection.Handle!.DangerousGetHandle();
            if (Deserialize(handle, "main", _buffer, image.Length, capacity, writable ? 0u : 4u) != 0)
                throw new InvalidDataException("SQLite could not open the private image.");
            SetProgress(handle, 1000, _progress, IntPtr.Zero);
            Limit(handle, 0, 4 * 1024 * 1024); // Individual text/blob/row length.
            Limit(handle, 1, 16384); Limit(handle, 2, 256); Limit(handle, 3, 64);
            Limit(handle, 4, 4); Limit(handle, 5, 20000); Limit(handle, 7, 0);
            Limit(handle, 10, writable ? 4 : 0); // SQLite implements foreign-key cascades with trigger programs.
            using var command = _connection.CreateCommand();
            command.CommandText = writable
                ? "PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY; PRAGMA journal_mode=MEMORY; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON;"
                : "PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY; PRAGMA query_only=ON;";
            command.ExecuteNonQuery();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Copies the committed writable image into a caller-owned buffer.</summary>
    public byte[] CopyCommittedImage()
    {
        var connection = Connection;
        if (!_writable) throw new InvalidOperationException("A writable SQLite copy is required.");
        var handle = connection.Handle!.DangerousGetHandle();
        if (GetAutocommit(handle) == 0) throw new InvalidOperationException("Commit the SQLite transaction before copying.");
        var address = Serialize(handle, "main", out var length, 1); // SQLITE_SERIALIZE_NOCOPY
        if (address != _buffer || length is < 512 or > MaximumImageBytes)
            throw new InvalidDataException("The committed SQLite image could not be copied.");

        var bytes = new byte[(int)length];
        Marshal.Copy(address, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Closes SQLite before clearing and releasing its owned memory buffer; pooling is disabled.</summary>
    public void Dispose()
    {
        if (_buffer == IntPtr.Zero) { _connection.Dispose(); return; }
        try { _connection.Dispose(); }
        finally
        {
            for (var offset = 0; offset < _length; offset += Zeros.Length)
                Marshal.Copy(Zeros, 0, IntPtr.Add(_buffer, offset), Math.Min(Zeros.Length, _length - offset));
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            GC.KeepAlive(_progress);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr context);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_deserialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Deserialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, IntPtr bytes, long length, long capacity, uint flags);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_serialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Serialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, out long length, uint flags);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_get_autocommit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetAutocommit(IntPtr db);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetProgress(IntPtr db, int instructions, Progress callback, IntPtr context);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_limit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Limit(IntPtr db, int category, int value);
}
