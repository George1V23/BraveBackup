using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class LinuxPasswordKeyTests
{
    [Fact]
    public void ExplicitBraveLookupDerivesVectorAndClearsSecret()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "Crypto.synthetic.json")));
        var secret = Encoding.UTF8.GetBytes(vectors.RootElement.GetProperty("syntheticLinuxSecret").GetString()!);
        using var key = BraveLinuxPasswordKey.Unlock((identity, token) =>
        {
            Assert.Equal("brave", identity);
            Assert.False(token.IsCancellationRequested);
            return secret;
        });
        Assert.All(secret, b => Assert.Equal(0, b));
        using var value = BravePasswordCrypto.Decode(Convert.FromHexString(vectors.RootElement.GetProperty("valuesHex").GetProperty("linux-v11-password").GetString()!), key);
        Assert.Equal(vectors.RootElement.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(value.Utf8.Span));
    }

    [Fact]
    public void MissingSecretNeverBecomesEmptyFallback()
    {
        var error = Assert.Throws<BackupOperationException>(() => BraveLinuxPasswordKey.Unlock((_, _) => null!));
        Assert.Equal("passwords-key-unlock-failed", error.Code);
    }

    [Fact]
    public void ExplicitEmptySecretIsDistinctFromMissing()
    {
        using var key = BraveLinuxPasswordKey.Unlock((_, _) => []);
        Assert.Equal(PasswordEncryptionProvider.LinuxKeyringV11, key.Provider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpectedFailuresAreContentFree(bool unavailable)
    {
        var error = Assert.Throws<BackupOperationException>(() => BraveLinuxPasswordKey.Unlock((_, _) =>
            throw (unavailable ? new PlatformNotSupportedException("synthetic private metadata") : new CryptographicException("synthetic private metadata"))));
        Assert.Equal("passwords-key-unlock-failed", error.Code);
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void CancellationClearsReturnedSecretAndNeverFallsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var secret = new byte[] { 42, 43 };
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => BraveLinuxPasswordKey.Unlock((_, _) =>
        { calls++; cancellation.Cancel(); return secret; }, cancellation.Token));
        Assert.All(secret, b => Assert.Equal(0, b));
        Assert.Throws<OperationCanceledException>(() => BraveLinuxPasswordKey.Unlock((_, _) =>
        { calls++; return []; }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OversizedReturnedSecretIsCleared()
    {
        var secret = Enumerable.Repeat((byte)42, 65537).ToArray();
        Assert.Throws<BackupOperationException>(() => BraveLinuxPasswordKey.Unlock((_, _) => secret));
        Assert.All(secret, b => Assert.Equal(0, b));
    }
}
