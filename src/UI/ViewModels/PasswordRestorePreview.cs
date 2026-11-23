using System.Linq;
using BraveBackup.Core;

namespace UI.ViewModels;

/// <summary>Describes the archived password representation and its Restore limitation without displaying credentials.</summary>
internal static class PasswordRestorePreview
{
    /// <summary>Builds the existing preview and category status from validated archive metadata.</summary>
    internal static string Describe(ArchiveCategory category, ArchivedPasswordInventory? inventory = null)
    {
        var (representation, limitation) = category.Files.Any(file => file.Role == "raw-password-index")
            ? ("Raw password files", "Recovery depends on the original OS protection and the condition of the captured files.")
            : category.Files.Any(file => file.Role == "plaintext-password-csv")
                ? ("Password CSV", "CSV credentials are readable after extraction. Brave's CSV import is a separate operation.")
                : ("Portable password payload", "Replacing saved logins requires compatible destination encryption and password-store handling.");
        var contents = inventory switch
        {
            ArchivedPasswordInventory.Portable portable => " " + string.Join(" ", portable.Stores.Select(Store)),
            ArchivedPasswordInventory.RawFiles raw => $" {Count(raw.FileCount, "native file", "native files")} preserved." +
                (raw.MissingMainDatabases > 0 ? $" {Count(raw.MissingMainDatabases, "main database", "main databases")} missing." : string.Empty),
            ArchivedPasswordInventory.Csv csv => $" {Count(csv.Rows, "CSV row", "CSV rows")}.",
            _ => string.Empty
        };
        var availability = inventory is ArchivedPasswordInventory.Portable { Stores: [{ Kind: PortablePasswordStoreKind.Profile }] }
            ? "Password restore requires a verified Linux destination."
            : "Password restore is unavailable.";
        return $"{representation}.{contents} {availability} {limitation}";
    }

    private static string Store(ArchivedPasswordStore store)
    {
        var name = store.Kind == PortablePasswordStoreKind.Profile ? "Profile store" : "Local account store";
        var included = $"{Count(store.Logins, "saved login", "saved logins")} and {Count(store.Notes, "note", "notes")}";
        var excluded = store.BlockedEntries + store.FederatedEntries == 0 ? string.Empty :
            $" Excluded {Count(store.BlockedEntries, "blocklisted entry", "blocklisted entries")} and " +
            $"{Count(store.FederatedEntries, "federated entry", "federated entries")}.";
        return $"{name} contains {included}.{excluded}";
    }

    private static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
}
