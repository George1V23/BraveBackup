namespace BraveBackup.Core;

/// <summary>Operation-scoped plaintext warning metadata for one archive destination; contains no credential values.</summary>
public sealed record PasswordCsvArchiveWarning(string Destination, bool ArchiveEncrypted,
    bool ReplacesExistingFile, int ProfileCount)
{
    public string Message =>
        $"Destination: {Destination}\nProfiles: {ProfileCount}\n\n" +
        "Passwords will be stored as plaintext CSV entries. Saved credentials will be readable by anyone with file access " +
        (ArchiveEncrypted ? "after decrypting the archive. Temporary archive staging remains encrypted. " :
            "to the unencrypted archive, including its temporary staging file. ") +
        (ReplacesExistingFile ? "The existing destination will be replaced. " : "") +
        "Save this archive with plaintext password CSV entries?";
}
