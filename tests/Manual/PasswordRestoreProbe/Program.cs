using System.Security.Cryptography;
using BraveBackup.Core;

// Only the Python probe invokes this helper. Its input is an isolated fixture key and database.
if (args is not ["--synthetic", "replace" or "empty"]) return 2;
var keyBytes = new byte[16];
try
{
    using var input = Console.OpenStandardInput();
    input.ReadExactly(keyBytes);
    using var key = new PasswordKeyMaterial(PasswordEncryptionProvider.LinuxKeyringV11, keyBytes);
    using var database = new MemoryStream();
    var buffer = new byte[65536];
    for (var read = input.Read(buffer); read > 0; read = input.Read(buffer))
    {
        if (database.Length + read > SqliteSnapshotImage.MaximumBytes) return 2;
        database.Write(buffer, 0, read);
    }

    const string origin = "https://password-restore.example.invalid/";
    var logins = args[1] == "empty" ? [] : new[]
    {
        new PortablePasswordLogin(1, origin, origin, "restored", null,
            new SensitiveUtf8("restored synthetic 🔒"u8),
            [new PortablePasswordNote("", 42, false, new SensitiveUtf8("restored synthetic note ✓"u8))]),
        new PortablePasswordLogin(2, origin, origin, "restored-empty", null,
            new SensitiveUtf8([]), [new PortablePasswordNote("", 42, false, new SensitiveUtf8([]))])
    };
    using var document = new PortablePasswordDocument([new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, logins)]);
    using var payload = PasswordPayloadFormat.Encode(document);
    var replacement = BravePasswordRestoreStage.Prepare(database.ToArray(), payload.Bytes.Span, key, DateTimeOffset.UtcNow);
    using var output = Console.OpenStandardOutput();
    output.Write(replacement);
    return 0;
}
catch (Exception error) when (error is IOException or BackupOperationException)
{
    Console.Error.WriteLine("The managed synthetic staging fixture failed.");
    return 1;
}
finally
{
    CryptographicOperations.ZeroMemory(keyBytes);
}
