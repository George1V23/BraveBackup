using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace BraveBackup.Platform;

/// <summary>
/// Owns a read-only native SQLite database backed by a private memory copy, never a source
/// filename. Callers must dispose all commands/readers before disposing this owner.
/// </summary>
public sealed class SqliteMemoryImage : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Progress _progress;
    private readonly int _length;
    private IntPtr _buffer;
    private long _steps;

    /// <summary>Gets the trusted caller's connection for fixed read-only queries; no source path is ever passed to SQLite.</summary>
    public SqliteConnection Connection => _buffer != IntPtr.Zero ? _connection : throw new ObjectDisposedException(nameof(SqliteMemoryImage));

    /// <summary>Copies a bounded assembled image into native memory and configures read-only, bounded execution.</summary>
    public SqliteMemoryImage(byte[] image, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (image.Length is < 512 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid memory image size.");
        _length = image.Length + 32; // SQLite documents padding for malformed input.
        _connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _progress = _ => token.IsCancellationRequested || Interlocked.Increment(ref _steps) > 100_000 ? 1 : 0;
        try
        {
            _buffer = Marshal.AllocHGlobal(_length);
            Marshal.Copy(image, 0, _buffer, image.Length);
            Marshal.Copy(new byte[32], 0, IntPtr.Add(_buffer, image.Length), 32);
            // SQLite explicitly documents this normalization for a deserialized WAL-mode image.
            Marshal.WriteByte(_buffer, 18, 1); Marshal.WriteByte(_buffer, 19, 1);
            _connection.Open();
            var handle = _connection.Handle!.DangerousGetHandle();
            if (Deserialize(handle, "main", _buffer, image.Length, _length, 4) != 0)
                throw new InvalidDataException("SQLite could not open the private image.");
            SetProgress(handle, 1000, _progress, IntPtr.Zero);
            Limit(handle, 0, 4 * 1024 * 1024); // Individual text/blob/row length.
            Limit(handle, 1, 16384); Limit(handle, 2, 256); Limit(handle, 3, 64);
            Limit(handle, 4, 4); Limit(handle, 5, 20000); Limit(handle, 7, 0); Limit(handle, 10, 0);
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY; PRAGMA query_only=ON;";
            command.ExecuteNonQuery();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Closes SQLite before clearing and releasing its owned memory buffer; pooling is disabled.</summary>
    public void Dispose()
    {
        if (_buffer == IntPtr.Zero) { _connection.Dispose(); return; }
        try { _connection.Dispose(); }
        finally
        {
            var zero = new byte[65536];
            for (var offset = 0; offset < _length; offset += zero.Length)
                Marshal.Copy(zero, 0, IntPtr.Add(_buffer, offset), Math.Min(zero.Length, _length - offset));
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            GC.KeepAlive(_progress);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr context);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_deserialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Deserialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, IntPtr bytes, long length, long capacity, uint flags);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetProgress(IntPtr db, int instructions, Progress callback, IntPtr context);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_limit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Limit(IntPtr db, int category, int value);
}
