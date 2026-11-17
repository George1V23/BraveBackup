# Portable password payload, schema 1

This is a new BraveBackup-owned interchange schema, not a claimed Brave/Chromium format.
It is implemented in Core by `PortablePasswordDocument` and `PasswordPayloadFormat`.
Existing archive versions 1–5 are unchanged: no archive entry or password capability is
enabled yet. A future archive version must separately document its password integration.

## Protection and ownership

The payload itself is **plaintext**, including metadata, passwords and notes. It is
intended for the approved protected option inside an encrypted archive, without plaintext
file staging. It is not encrypted merely because it uses a binary representation. No
file-saving API, CSV implementation, consent shortcut or archive writer is added here.
An unencrypted save is not authorized by selecting Passwords or invoking this serializer.

`PortablePasswordDocument` copies collection structures and takes ownership of every
supplied `SensitiveUtf8` password/note. Callers must transfer ownership without sharing
those secret owners. Disposal clears secrets and prevents access through `Stores`;
borrowed references/copies must be handled according to their ownership contract.
Metadata remains managed strings and cannot be reliably erased; never log it.

`PasswordPayloadFormat.Encode` validates the document, measures its exact size and creates
one owned payload buffer. It never creates plaintext password/note strings, uses growing
stream buffers or accesses the filesystem. `SensitiveBytes` clears the serialized buffer
on disposal; failure/cancellation clears the allocation. `Decode` creates independent
secret owners and disposes partial secrets on failure. It never modifies input bytes.
Redacted `ToString` methods do not make other credential inspection safe for logging.
Callers must not mutate/dispose inputs concurrently with encoding, decoding or mapping.

`BravePortablePasswords.Create` in Backup copies a `BravePasswordCapture` into this Core
contract, mapping the two allowlisted local store names to portable store identities.
It verifies sources before and after copying and clears partial copies on failure.
Disposing the source capture does not invalidate the portable copy. Future publication
still needs the capture's final `VerifyUnchanged` and the archive's atomic-save checks.

## Binary specification

All integers are signed little-endian; counts/lengths must be nonnegative except the
explicit nullable text sentinel. There is no padding or trailing data. Order is retained.

| Field | Representation |
| --- | --- |
| Magic/schema | Eight ASCII bytes `BBKPW001` |
| Store count | int32, one or two |
| Each store | kind int32 (`0` profile, `1` account), blocked count int32, federated count int32, login count int32, followed by logins |
| Each login | source ID int64, origin text, realm text, nullable username text, nullable display name text, password secret, note count int32, followed by notes |
| Each note | name text, original creation timestamp int64, confidentiality int32 (`0` or `1`), value secret |
| Text | UTF-8 byte length int32 then exactly those bytes; `-1` means null, allowed only for username/display name |
| Secret | UTF-8 byte length int32 then exactly those bytes; empty is valid, null is not |

Timestamps preserve source integer values without claiming a new epoch or timezone.
Unicode, embedded NUL, CR/LF, quotes and commas are preserved exactly. A separate note
remains separate, including its identity and confidentiality flag. No note flattening,
URL affiliation expansion, deduplication or CSV projection occurs. Store kind identifies
local source storage, not the availability/completeness of cloud credentials.

Reject unknown magic/schema, store kinds, duplicate store kinds, duplicate/nonpositive
login IDs within a store, duplicate note names within a login, invalid confidentiality
flags, missing required text, malformed UTF-8, negative/oversized lengths or counts,
truncation and trailing bytes. Do not silently omit invalid entries.

Limits: 64 MiB payload, 32 MiB combined secret bytes, 4 MiB per secret, 1 MiB UTF-8 per
metadata field, 100,000 total login-plus-blocked-plus-federated entries, and 100,000 notes.
Lengths/counts are checked before reading or allocating their individual contents.
Complete document validation also runs after decoding; aggregate ownership stays bounded
by the input payload limit while decoding. Failure does not return a partial document.

## Fixtures and validation

`tests/Core.Tests/Fixtures/PortablePasswords/generate-fixture.py` independently implements
the specification with Python `struct`; `Payload.v1.synthetic.bin` contains only synthetic
data. Regenerate with Python, then test the .NET writer's exact byte equality and reader's
field preservation. It is evidence for this application's schema, not a browser fixture.

Twenty-four codec tests cover exact independent bytes, null/empty metadata, Unicode/NUL,
separate notes and exclusion counts, round trips, every truncated prefix, trailing bytes,
unknown versions/kinds/counts, malformed metadata/secrets, duplicate identities, limits,
cancellation and owned-buffer clearing. Three capture integration tests cover both
provider fixtures, independent copies, round-trip serialization, stale-source rejection
and cancellation. No real profile or keyring is accessed.

Password CSV projection/consent, the explicit raw-file fallback, archive format integration,
UI controls and real native authentication validation remain pending. Password backup
remains disabled in the UI.
