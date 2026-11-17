using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit Secret Service v11 key lookup using the researched Brave application identity.</summary>
public static class BraveLinuxPasswordKey
{
    /// <summary>Looks up an existing secret and derives a v11 key. Never creates keys or tries other backends.</summary>
    /// <remarks>Lookup returns owned bytes cleared here. Run off the UI thread; a native unlock prompt may appear.</remarks>
    public static PasswordKeyMaterial Unlock(Func<string, CancellationToken, byte[]>? lookup = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        byte[]? secret = null;
        try
        {
            secret = lookup is null ? LinuxSecretService.LookupExisting("brave", token) : lookup("brave", token);
            token.ThrowIfCancellationRequested();
            if (secret is null || secret.Length > 65536) throw Failure();
            return PasswordKeyMaterial.FromLinuxKeyringSecret(secret);
        }
        catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException)
        { throw Failure(); }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }

    private static BackupOperationException Failure() => new("passwords-key-unlock-failed",
        "The existing Brave Secret Service key could not be unlocked. No other backend or replacement key was used.");
}
