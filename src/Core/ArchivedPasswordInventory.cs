namespace BraveBackup.Core;

/// <summary>Value-free metadata from an already validated password archive category.</summary>
public abstract record ArchivedPasswordInventory
{
    private ArchivedPasswordInventory() { }

    /// <summary>Counts for each store represented in a protected portable payload.</summary>
    public sealed record Portable(IReadOnlyList<ArchivedPasswordStore> Stores) : ArchivedPasswordInventory;

    /// <summary>Counts preserved native files and missing main databases identified by the validated index.</summary>
    public sealed record RawFiles(int FileCount, int MissingMainDatabases) : ArchivedPasswordInventory;

    /// <summary>Counts validated CSV data rows. Rows need not correspond one-to-one to source logins.</summary>
    public sealed record Csv(int Rows) : ArchivedPasswordInventory;
}

/// <summary>Store identity and counts without credentials, identifiers, origins or note contents.</summary>
public sealed record ArchivedPasswordStore(PortablePasswordStoreKind Kind, int Logins, int Notes,
    int BlockedEntries, int FederatedEntries);
