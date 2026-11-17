using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Native tests use only freshly generated synthetic bytes, never Brave or user credential stores.</summary>
public sealed class WindowsDpapiTests
{
    [WindowsOnlyFact]
    public void SyntheticCurrentUserBlobRoundTrips()
    {
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var wrapped = Protect(plaintext);
        byte[]? decoded = null;
        try
        {
            decoded = WindowsDpapi.Unprotect(wrapped);
            Assert.Equal(plaintext, decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(wrapped);
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
        }
    }

    [WindowsOnlyFact]
    public void InvalidBlobFailsWithContentFreeError()
    {
        var error = Assert.Throws<CryptographicException>(() => WindowsDpapi.Unprotect("synthetic-invalid-dpapi-blob"u8));
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.Null(error.InnerException);
    }

    private static byte[] Protect(byte[] value)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new CryptographicException("Synthetic DPAPI fixture creation failed.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}

/// <summary>Marks actual Windows execution as required rather than counting a no-op Linux run as native coverage.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows DPAPI; no emulation or real user credentials.";
    }
}
