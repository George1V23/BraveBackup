using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BraveBackup.Platform;

/// <summary>Lookup-only access to an existing default-collection Secret Service item on explicit invocation.</summary>
public static class LinuxSecretService
{
    /// <summary>Returns owned secret bytes; caller must clear them. May block for a native keyring prompt.</summary>
    /// <remarks>Run on a worker, never the UI thread. No keys/collections are created; ambiguous matches fail.</remarks>
    public static byte[] LookupExisting(string application, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux Secret Service is unavailable.");
        if (string.IsNullOrEmpty(application) || application.Length > 128 || application.Contains('\0'))
            throw new ArgumentException("Invalid Secret Service application identity.", nameof(application));
        try
        {
            return Lookup(application, token);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("Required Secret Service native libraries are unavailable.");
        }
    }

    private static byte[] Lookup(string application, CancellationToken token)
    {
        var cancellable = g_cancellable_new();
        if (cancellable == IntPtr.Zero) throw Failure();
        try
        {
            using var registration = token.Register(() => g_cancellable_cancel(cancellable));
            IntPtr service = IntPtr.Zero, collection = IntPtr.Zero, attributes = IntPtr.Zero, list = IntPtr.Zero;
            var name = Marshal.StringToCoTaskMemUTF8("application");
            var identity = Marshal.StringToCoTaskMemUTF8(application);
            byte[]? result = null;
            try
            {
                // A private proxy avoids retaining loaded secrets in libsecret's shared singleton proxy cache.
                service = secret_service_open_sync(secret_service_get_type(), "org.freedesktop.secrets", 0, cancellable,
                    out var error);
                Check(error, token);
                if (service == IntPtr.Zero) throw Failure();
                collection = secret_collection_for_alias_sync(service, "default", 0, cancellable, out error);
                Check(error, token);
                if (collection == IntPtr.Zero) throw Failure();
                attributes = g_hash_table_new(StringHash, StringEqual);
                if (attributes == IntPtr.Zero) throw Failure();
                _ = g_hash_table_insert(attributes, name, identity);
                // ALL | UNLOCK | LOAD_SECRETS. A cancelled/failed unlock may return a locked item: reject below.
                list = secret_collection_search_sync(collection, IntPtr.Zero, attributes, 14, cancellable, out error);
                Check(error, token);
                if (list == IntPtr.Zero) throw Failure();
                var item = Marshal.PtrToStructure<NativeList>(list);
                if (item.Next != IntPtr.Zero || item.Data == IntPtr.Zero) throw Failure();
                var value = secret_item_get_secret(item.Data);
                if (value == IntPtr.Zero) throw Failure();
                try
                {
                    var data = secret_value_get(value, out var length);
                    if (length > 65536 || (length != 0 && data == IntPtr.Zero)) throw Failure();
                    result = new byte[(int)length];
                    if (result.Length != 0) Marshal.Copy(data, result, 0, result.Length);
                }
                finally
                {
                    secret_value_unref(value);
                }

                token.ThrowIfCancellationRequested();
                var owned = result;
                result = null;
                return owned;
            }
            finally
            {
                if (result is not null) CryptographicOperations.ZeroMemory(result);
                for (var node = list; node != IntPtr.Zero;)
                {
                    var entry = Marshal.PtrToStructure<NativeList>(node);
                    if (entry.Data != IntPtr.Zero) g_object_unref(entry.Data);
                    node = entry.Next;
                }

                if (list != IntPtr.Zero) g_list_free(list);
                if (attributes != IntPtr.Zero) g_hash_table_unref(attributes);
                if (collection != IntPtr.Zero) g_object_unref(collection);
                if (service != IntPtr.Zero) g_object_unref(service);
                Marshal.FreeCoTaskMem(identity);
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            g_object_unref(cancellable);
        }
    }

    private static void Check(IntPtr error, CancellationToken token)
    {
        if (error != IntPtr.Zero) g_error_free(error); // Never copy native error text: it can include private metadata.
        token.ThrowIfCancellationRequested();
        if (error != IntPtr.Zero) throw Failure();
    }

    private static CryptographicException Failure() =>
        new("An existing unambiguous Secret Service item could not be unlocked.");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeList
    {
        public IntPtr Data;
        public IntPtr Next;
        public IntPtr Previous;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint HashCallback(IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EqualCallback(IntPtr first, IntPtr second);

    private static readonly HashCallback StringHash = g_str_hash;
    private static readonly EqualCallback StringEqual = g_str_equal;

    private const string Secret = "libsecret-1.so.0",
        Glib = "libglib-2.0.so.0",
        Gio = "libgio-2.0.so.0",
        GObject = "libgobject-2.0.so.0";

    [DllImport(Secret)]
    private static extern nuint secret_service_get_type();

    [DllImport(Secret)]
    private static extern IntPtr secret_service_open_sync(nuint type, [MarshalAs(UnmanagedType.LPUTF8Str)] string bus,
        int flags, IntPtr cancel, out IntPtr error);

    [DllImport(Secret)]
    private static extern IntPtr secret_collection_for_alias_sync(IntPtr service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alias, int flags, IntPtr cancel, out IntPtr error);

    [DllImport(Secret)]
    private static extern IntPtr secret_collection_search_sync(IntPtr collection, IntPtr schema, IntPtr attributes,
        int flags, IntPtr cancel, out IntPtr error);

    [DllImport(Secret)]
    private static extern IntPtr secret_item_get_secret(IntPtr item);

    [DllImport(Secret)]
    private static extern IntPtr secret_value_get(IntPtr value, out nuint length);

    [DllImport(Secret)]
    private static extern void secret_value_unref(IntPtr value);

    [DllImport(Gio)]
    private static extern IntPtr g_cancellable_new();

    [DllImport(Gio)]
    private static extern void g_cancellable_cancel(IntPtr value);

    [DllImport(GObject)]
    private static extern void g_object_unref(IntPtr value);

    [DllImport(Glib)]
    private static extern IntPtr g_hash_table_new(HashCallback hash, EqualCallback equal);

    [DllImport(Glib)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(Glib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(Glib)]
    private static extern uint g_str_hash(IntPtr value);

    [DllImport(Glib)]
    private static extern int g_str_equal(IntPtr first, IntPtr second);

    [DllImport(Glib)]
    private static extern void g_list_free(IntPtr list);

    [DllImport(Glib)]
    private static extern void g_error_free(IntPtr error);
}