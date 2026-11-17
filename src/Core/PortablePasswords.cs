namespace BraveBackup.Core;

/// <summary>Locally captured store identity, without a machine path or OS key.</summary>
public enum PortablePasswordStoreKind { Profile = 0, Account = 1 }

/// <summary>Portable login metadata and owned secret values. Never prints credential contents.</summary>
public sealed class PortablePasswordLogin(long id, string origin, string realm, string? username,
    string? displayName, SensitiveUtf8 password, IEnumerable<PortablePasswordNote> notes)
{
    public long Id { get; } = id;
    public string Origin { get; } = origin;
    public string Realm { get; } = realm;
    public string? Username { get; } = username;
    public string? DisplayName { get; } = displayName;
    public SensitiveUtf8 Password { get; } = password;
    public IReadOnlyList<PortablePasswordNote> Notes { get; } = Array.AsReadOnly(notes.ToArray());
    public override string ToString() => "Portable login (contents hidden)";
}

/// <summary>One note retaining its original name, timestamp and confidentiality flag.</summary>
public sealed class PortablePasswordNote(string name, long created, bool confidential, SensitiveUtf8 value)
{
    public string Name { get; } = name;
    public long Created { get; } = created;
    public bool Confidential { get; } = confidential;
    public SensitiveUtf8 Value { get; } = value;
    public override string ToString() => "Portable password note (contents hidden)";
}

/// <summary>One local store and safe exclusion counts; does not claim cloud credentials were captured.</summary>
public sealed class PortablePasswordStore(PortablePasswordStoreKind kind, int blockedEntries,
    int federatedEntries, IEnumerable<PortablePasswordLogin> logins)
{
    public PortablePasswordStoreKind Kind { get; } = kind;
    public int BlockedEntries { get; } = blockedEntries;
    public int FederatedEntries { get; } = federatedEntries;
    public IReadOnlyList<PortablePasswordLogin> Logins { get; } = Array.AsReadOnly(logins.ToArray());
    public override string ToString() => "Portable password store (contents hidden)";
}

/// <summary>Owns all supplied password/note values. Ownership transfers on construction; do not share or mutate them.</summary>
public sealed class PortablePasswordDocument : IDisposable
{
    private IReadOnlyList<PortablePasswordStore>? _stores;
    public IReadOnlyList<PortablePasswordStore> Stores => _stores ?? throw new ObjectDisposedException(nameof(PortablePasswordDocument));

    /// <summary>Copies collection structure and takes ownership of the supplied secret owners, not their callers' copies.</summary>
    public PortablePasswordDocument(IEnumerable<PortablePasswordStore> stores) => _stores = Array.AsReadOnly(stores.ToArray());

    /// <summary>Clears every owned password and note; borrowed buffers must not be used concurrently with disposal.</summary>
    public void Dispose()
    {
        if (_stores is null) return;
        foreach (var store in _stores)
        foreach (var login in store.Logins)
        {
            login.Password.Dispose();
            foreach (var note in login.Notes) note.Value.Dispose();
        }
        _stores = null;
    }
    public override string ToString() => "Portable password document (contents hidden)";
}
