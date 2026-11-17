namespace BraveBackup.Core;

/// <summary>Copies a verified Brave capture into the browser-independent portable document contract.</summary>
public static class BravePortablePasswords
{
    /// <summary>Creates independent secret owners, clearing partial copies on failure and checking sources before/after.</summary>
    /// <remarks>This does not authorize local saving. Recheck the capture again before future archive publication.</remarks>
    public static PortablePasswordDocument Create(BravePasswordCapture capture, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        token.ThrowIfCancellationRequested();
        capture.VerifyUnchanged();
        var values = new List<SensitiveUtf8>();
        try
        {
            var stores = new List<PortablePasswordStore>();
            foreach (var entry in capture.Databases)
            {
                token.ThrowIfCancellationRequested();
                var kind = entry.Key switch
                {
                    "Login Data" => PortablePasswordStoreKind.Profile,
                    "Login Data For Account" => PortablePasswordStoreKind.Account,
                    _ => throw new InvalidDataException("Unsupported local password store identity.")
                };
                var logins = new List<PortablePasswordLogin>();
                foreach (var login in entry.Value.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    var password = Copy(login.Password);
                    var notes = login.Notes.Select(note => new PortablePasswordNote(note.Name, note.Created, note.Confidential, Copy(note.Value))).ToList();
                    logins.Add(new PortablePasswordLogin(login.Id, login.Origin, login.Realm, login.Username, login.DisplayName, password, notes));
                }
                stores.Add(new PortablePasswordStore(kind, entry.Value.BlockedEntries, entry.Value.FederatedEntries, logins));
            }
            token.ThrowIfCancellationRequested();
            capture.VerifyUnchanged();
            return new PortablePasswordDocument(stores);
        }
        catch { foreach (var value in values) value.Dispose(); throw; }

        SensitiveUtf8 Copy(SensitiveUtf8 source)
        {
            token.ThrowIfCancellationRequested();
            var value = new SensitiveUtf8(source.Utf8.Span); values.Add(value); return value;
        }
    }
}
