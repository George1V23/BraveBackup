using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BraveBackup.Platform;

/// <summary>Explicit current-user Windows DPAPI access. Does not read files or collect OS credentials.</summary>
public static class WindowsDpapi
{
    /// <summary>Unprotects a bounded existing blob. The caller owns and must clear the returned bytes.</summary>
    /// <remarks>Uses the existing logon context, not a guaranteed fresh authentication prompt.</remarks>
    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is unavailable on this platform.");
        if (protectedData.IsEmpty || protectedData.Length > 65536)
            throw new ArgumentException("Invalid protected key length.", nameof(protectedData));
        var input = new DataBlob { Length = protectedData.Length, Data = Marshal.AllocHGlobal(protectedData.Length) };
        var output = new DataBlob();
        var copy = protectedData.ToArray();
        try
        {
            Marshal.Copy(copy, 0, input.Data, copy.Length);
            // No deprecated prompt structure and no UI suppression: this is native current-logon DPAPI.
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output))
                throw new CryptographicException("The current Windows user could not unlock the browser key.");
            if (output.Length <= 0 || output.Length > 65536 || output.Data == IntPtr.Zero)
                throw new CryptographicException("Windows returned an invalid browser key result.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
            Clear(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                Clear(output.Data, output.Length);
                _ = LocalFree(output.Data);
            }
        }
    }

    private static void Clear(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++) Marshal.WriteByte(pointer, index, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
