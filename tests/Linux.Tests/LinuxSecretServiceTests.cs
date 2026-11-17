using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Native calls run only under the isolated synthetic D-Bus fixture launcher.</summary>
public sealed class LinuxSecretServiceTests
{
    [IsolatedSecretServiceFact]
    public void ExistingSecretUsesNativeLookup()
    {
        var bytes = LinuxSecretService.LookupExisting("brave");
        try { Assert.Equal("synthetic-keyring-secret-not-real", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [IsolatedSecretServiceFact]
    public void MissingAndAmbiguousAndLockedItemsFail()
    {
        foreach (var app in new[] { "missing", "ambiguous", "locked", "oversized" })
        {
            var error = Assert.Throws<CryptographicException>(() => LinuxSecretService.LookupExisting(app));
            Assert.Equal("An existing unambiguous Secret Service item could not be unlocked.", error.Message);
            Assert.Null(error.InnerException);
        }
    }

    [IsolatedSecretServiceFact]
    public void ExplicitEmptyAndBinarySecretsPreserveBytes()
    {
        Assert.Empty(LinuxSecretService.LookupExisting("empty"));
        var bytes = LinuxSecretService.LookupExisting("binary");
        try { Assert.Equal(new byte[] { 0, 255, 1, 0 }, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [IsolatedSecretServiceFact]
    public void CancellationInterruptsPendingNativeSearch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.Throws<OperationCanceledException>(() => LinuxSecretService.LookupExisting("slow", cancellation.Token));
    }

    [Fact]
    public void PreCancelledLookupNeverContactsService()
    {
        Assert.Throws<OperationCanceledException>(() => LinuxSecretService.LookupExisting("brave", new CancellationToken(true)));
        if (OperatingSystem.IsLinux())
        {
            Assert.Throws<ArgumentException>(() => LinuxSecretService.LookupExisting("bad\0identity"));
            Assert.Throws<ArgumentException>(() => LinuxSecretService.LookupExisting(new string('a', 129)));
        }
    }
}

public sealed class IsolatedSecretServiceFactAttribute : FactAttribute
{
    public IsolatedSecretServiceFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("BRAVEBACKUP_SYNTHETIC_SECRET_BUS") != "1")
            Skip = "Requires isolated synthetic Secret Service launcher; never use the desktop keyring for tests.";
    }
}
