using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class WindowsPasswordKeyTests
{
    private static byte[] State(string encoded) => Encoding.UTF8.GetBytes("{\"os_crypt\":{\"encrypted_key\":\"" + encoded + "\"}}");
    private static byte[] ValidState() => State(Convert.ToBase64String("DPAPIsynthetic-wrapped-key"u8));

    [Fact]
    public void UsesOnlyWrappedBytesAndClearsBothCallbackBuffers()
    {
        var input = ValidState();
        var original = input.ToArray();
        byte[]? received = null;
        var returned = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var key = BraveWindowsPasswordKey.Unlock(input, bytes =>
        {
            received = bytes;
            Assert.Equal("synthetic-wrapped-key"u8.ToArray(), bytes);
            return returned;
        });
        Assert.Equal(original, input);
        Assert.All(received!, b => Assert.Equal(0, b));
        Assert.All(returned, b => Assert.Equal(0, b));
        // The resulting owner copied the key before callback storage was cleared.
        var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Passwords", "Crypto.synthetic.json")));
        using (fixture)
        using (var value = BravePasswordCrypto.Decode(Convert.FromHexString(fixture.RootElement.GetProperty("valuesHex")
                   .GetProperty("windows-v10-password").GetString()!), key))
            Assert.Equal(fixture.RootElement.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(value.Utf8.Span));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"os_crypt\":null}")]
    [InlineData("{\"os_crypt\":{\"encrypted_key\":3}}")]
    [InlineData("{\"os_crypt\":{\"encrypted_key\":\"bad-base64\"}}")]
    [InlineData("{\"os_crypt\":{},\"os_crypt\":{}}")]
    [InlineData("{\"os_crypt\":{\"encrypted_key\":\"\",\"encrypted_key\":\"\"}}")]
    public void InvalidEnvelopeNeverInvokesOsAccess(string json)
    {
        var called = false;
        var error = Assert.Throws<BackupOperationException>(() => BraveWindowsPasswordKey.Unlock(Encoding.UTF8.GetBytes(json), _ => { called = true; return []; }));
        Assert.Equal("passwords-key-envelope-unsupported", error.Code);
        Assert.False(called);
    }

    [Theory]
    [InlineData("APPBsynthetic")]
    [InlineData("DPAPI")]
    [InlineData("other")]
    public void DoesNotGuessAppBoundOrOtherEnvelope(string envelope)
    {
        Assert.Equal("passwords-key-envelope-unsupported", Assert.Throws<BackupOperationException>(() =>
            BraveWindowsPasswordKey.Unlock(State(Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope))), _ => throw new Exception("Must not access OS"))).Code);
    }

    [Fact]
    public void UnlockFailureIsContentFreeAndClearsInput()
    {
        byte[]? received = null;
        var error = Assert.Throws<BackupOperationException>(() => BraveWindowsPasswordKey.Unlock(ValidState(), bytes =>
        { received = bytes; throw new CryptographicException("synthetic secret must not escape"); }));
        Assert.Equal("passwords-key-unlock-failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.All(received!, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(33)]
    public void InvalidReturnedKeyIsCleared(int length)
    {
        var returned = Enumerable.Repeat((byte)42, length).ToArray();
        Assert.Equal("passwords-key-unlock-failed", Assert.Throws<BackupOperationException>(() =>
            BraveWindowsPasswordKey.Unlock(ValidState(), _ => returned)).Code);
        Assert.All(returned, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CancellationAfterUnlockClearsReturnedKey()
    {
        using var cancellation = new CancellationTokenSource();
        var returned = Enumerable.Repeat((byte)42, 32).ToArray();
        Assert.Throws<OperationCanceledException>(() => BraveWindowsPasswordKey.Unlock(ValidState(), _ =>
        { cancellation.Cancel(); return returned; }, cancellation.Token));
        Assert.All(returned, b => Assert.Equal(0, b));
        Assert.Throws<OperationCanceledException>(() => BraveWindowsPasswordKey.Unlock(ValidState(), _ => throw new Exception(), cancellation.Token));
    }

    [Fact]
    public void BoundedStateAndNonWindowsGuard()
    {
        Assert.Throws<BackupOperationException>(() => BraveWindowsPasswordKey.Unlock(new byte[4 * 1024 * 1024 + 1], _ => throw new Exception()));
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => WindowsDpapi.Unprotect("synthetic"u8));
    }
}
