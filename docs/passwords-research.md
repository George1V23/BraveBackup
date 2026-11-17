# Passwords research and fixture checkpoint

Completed source/fixture checkpoint, 2026-11-17, under the accepted
[seven scope decisions](passwords-backup-proposal.md). No application credential reader,
OS unlock, Selenium session, password export, archive format or package was added here.
Passwords remains unavailable in Create Backup. No real user credential database,
Local State key, browser profile or keyring was inspected.
The subsequent [snapshot checkpoint](passwords-snapshot.md) implements isolated encrypted
database capture/schema checks and documents the SQLite packages introduced there.

## Revisions actually inspected

Brave development source revision `a9f619b0c086e98678e89caf332b5badb1be1cea` identifies
source version 1.102.13 and Chromium tag 158.0.8127.37 in
[package.json](https://github.com/brave/brave-core/blob/a9f619b0c086e98678e89caf332b5badb1be1cea/package.json).
The corresponding Chromium revision is `b8c2be851f62f6a394ec57955d7f31335d7cb6d4`.
These development sources are not proof of the user's installed/released Brave version.

The earlier exploratory Chromium revision `c0837d415025e31505c769f6ef13ba0196feaea2`
reports Chromium 160.0.8167.0 and must not be described as Brave's matched dependency.
Critical schema, encryption-provider and export findings were rechecked at the matched
158 revision above. `Fixtures/Passwords/Source-evidence.json` freezes revisions and hashes.

## Database and credential meaning

Matched primary sources:

- [LoginDatabase and migrations](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_store/login_database.cc)
  and [v43 SQL test schema](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/test/data/password_manager/login_db_v43.sql):
  current schema 43, compatible version 40, `meta`, `logins` and related tables.
  The version is database metadata, not the browser's version number.
- [Password notes](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_store/password_notes_table.cc):
  notes have their own encrypted value and parent-login relationship. Version 40
  migrated notes to OSCrypt. Upstream may skip undecryptable notes; this app must
  instead follow the approved explicit failure/raw-preservation choice.
- [Store filenames](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_manager_constants.cc)
  define `Login Data`, `Login Data For Account` and their rollback-journal filenames.
  [Store creation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/password_store_factory_util.cc)
  distinguishes profile/account stores. Locally present account-store data is not
  automatically cloud-only data, and its authoritative use in a supported Brave build
  must be verified before claiming capture completeness.
-

Brave's [factory patch](https://github.com/brave/brave-core/blob/a9f619b0c086e98678e89caf332b5badb1be1cea/patches/components-password_manager-core-browser-password_store_factory_util.cc.patch)
and [override](https://github.com/brave/brave-core/blob/a9f619b0c086e98678e89caf332b5badb1be1cea/chromium_src/components/password_manager/core/browser/password_store_factory_util.cc)
were read. They do not establish an application-side read-only API. Chromium's full
LoginDatabase must not simply be instantiated against a user's profile: its normal
behavior includes migrations, encryption updates and undecryptable-data policy.

The initial reader candidate should validate the researched metadata/schema and query
only required columns. Blocklisted sites and federated-only entries are distinct from
saved password entries; report excluded-type counts without revealing values. Do not
deduce that an empty password blob is a corrupt or excluded record solely from its length.
Preserve actual empty credentials when the source type supports them. Unknown schema,
missing/corrupt tables or unreadable password/notes must not silently become zero.

## OS protection: candidate matrix, not implemented support

The
matched [encryptor](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/common/encryptor.cc)
dispatches by provider tag and algorithm. **`v10` alone does not identify an algorithm
across operating systems.** Resolve source platform/provider, key availability and format
together; unknown variants use the explicit failure path rather than guessed decryption.
CBC padding is not authentication. Successful synthetic decoding does not prove that
a guessed key is correct; provider identity and authoritative key lookup must be verified.
Do not try alternative keys/providers silently when the selected provider cannot decrypt.

| Source/provider                 | Source-defined behavior                                                                                                                    | Evidence and implementation status                                                                                                                                             |
|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Windows DPAPI-backed `v10`      | Local State contains a DPAPI-wrapped key; record encryption uses AES-256-GCM with a 12-byte nonce and authentication tag.                  | Matched source read; independent synthetic AES-GCM vectors verified. Current-user DPAPI/key access and real Windows behavior are unimplemented/untested.                       |
| Windows app-bound `v20`         | Browser provider uses app-bound key protection and path/isolation validation.                                                              | Source read. No ordinary application-access route demonstrated; do not claim that entering an OS password alone enables it. Offer approved fallback choices when inaccessible. |
| Linux Freedesktop/KWallet `v11` | Existing keyring secret derives an AES-128-CBC key via the source-defined PBKDF2 parameters; fixed IV and padding come from the encryptor. | Brave-specific names checked; independent synthetic CBC vectors verified. The [Secret Service adapter](passwords-linux-key.md) has isolated native coverage; real prompting and direct KWallet access remain unverified.                                     |
| Linux POSIX `v10`               | Source-defined legacy/basic AES-128-CBC provider, not Windows GCM.                                                                         | Source read only. Do not change the browser's password-store setting or select this as a fallback algorithm for unknown data.                                                  |
| Linux secret portal `v12`       | Separate portal/provider flow and derived AES-256-GCM key.                                                                                 | Source read only; portal identity/session behavior and key access remain unverified. Treat as unsupported until fixture and platform coverage exist.                           |
| Other legacy/provider variants  | May have different access and encoding rules.                                                                                              | Not covered by this checkpoint; explicit failure/raw preservation rather than speculative support.                                                                             |

Provider sources:
[DPAPI](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/dpapi_key_provider.cc),
[app-bound](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/os_crypt/app_bound_encryption_provider_win.cc),
[Freedesktop](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/freedesktop_secret_key_provider.cc),
[POSIX](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/posix_key_provider.cc),
[portal](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/secret_portal_key_provider.cc),
and [provider registration](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/browser_process_impl.cc).

Brave's [Freedesktop-name patch](https://github.com/brave/brave-core/blob/a9f619b0c086e98678e89caf332b5badb1be1cea/patches/components-os_crypt-async-browser-freedesktop_secret_key_provider.h.patch)
uses Brave-specific identity rather than Chromium names. Match the researched existing
item precisely. Lookup must not initialize/create/replace a missing browser key: the
upstream browser providers also contain write/initialization logic unsuitable for blind reuse.

### Authentication is provider-specific

The [Secret Service prompt protocol](https://specifications.freedesktop.org/secret-service/latest/prompts.html)
lets the service display its own prompt and report dismissal. An already-unlocked item
may need no prompt. KWallet/portal backends need their own verified integration; absence
or denial stops credential access. No app-owned OS-password collection is proposed.

[CryptUnprotectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata)
normally uses the current Windows logon context, not a universal new password-entry UI.
Its old optional prompt flow is deprecated. Any separate OS user-presence authentication
must be implemented using a supported API and must not be advertised as a way to make
an inaccessible app-bound key available. The explicit operation remains required even
when the OS does not present a prompt.

## Live capture and the no-profile-write requirement

[SQLite WAL documentation](https://sqlite.org/wal.html) describes transaction end marks,
committed WAL frames and shared-memory coordination. The
[online backup API](https://sqlite.org/backup.html) can produce a consistent in-memory
snapshot while another connection writes. Copying just the database loses newer WAL
commits. Adding `immutable=1` to a **changing live source** is not a valid shortcut:
[SQLite URI documentation](https://sqlite.org/uri.html) requires the immutability assumption
to be true and warns of incorrect results otherwise.

The synthetic SQLite 3.53.4 Linux experiment demonstrated:

1. A pinned read transaction retained its earlier value while a writer committed an update.
2. A subsequent read-only connection backup into memory saw the committed WAL value.
3. A main-database-only copy still showed the stale earlier value.
4. The read left main database and WAL bytes unchanged **but changed `-shm` bytes**.

Results are frozen in `Wal.experiment.json`. This is evidence that ordinary `mode=ro`
alone is insufficient for this project's strict no-profile-write rule, not proof that
live capture is impossible. Live capture must remain conditional on a tested strategy
that also prevents source shared-memory creation/modification/recovery writes. A suitable
read-only VFS or consistent private snapshot strategy needs implementation/proof first.
Do not enable a live path based only on the connection's read-only flag or unchanged DB
hash. If no compliant strategy is available, ask the user to close Brave and retry, as
already approved. Do not weaken the requirement or force a checkpoint on the original.

For closed-source capture, safely collect regular database/companion files into bounded
private storage/memory, verify the source set remained unchanged, and let SQLite work
only against the isolated copy. Prefer memory; any native-file staging must preserve
the protection/consent guarantees and private permissions. Never decrypt credentials
into an unconfirmed plaintext staging database. Hot rollback journals require a private
recovery path or explicit unsupported handling; never recover/migrate the original.

## Browser-assisted CSV export investigation

Selenium/ChromeDriver can choose a binary and custom data directory:
[Selenium documentation](https://www.selenium.dev/documentation/webdriver/browsers/chrome/),
[ChromeDriver capabilities](https://developer.chrome.com/docs/chromedriver/capabilities).
That establishes general browser automation, not a stable public Brave password-export API.
Matched [export/authentication implementation](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/chrome/browser/extensions/api/passwords_private/passwords_private_delegate_impl.cc)
requires platform-specific export authentication. Native dialogs are not ordinary web
elements, and visible user interaction may be necessary. Linux's export reauthentication
branch differs from Windows; keyring unlocking is a separate concern.

Source-level findings do **not** yet establish a verified Selenium fallback. No browser
was launched and no Selenium/driver dependency was installed in this checkpoint.
Outstanding requirements are version-compatible page automation, native authentication,
output completeness, cleanup, preserved original profiles and protected-key access from
an isolated copy. Launching the original profile can change it; a copied profile is not
guaranteed to retain usable key access. Chrome's
[default-profile debugging restrictions](https://developer.chrome.com/blog/remote-debugging-port)
are additional compatibility context, not proof that every Brave build has identical behavior.

The browser's CSV export itself writes plaintext locally. It therefore cannot silently
serve as a protected-default backup staging step: even if the final archive is encrypted,
the user must explicitly choose/confirm that local CSV save. Cleanup is not a secure-erasure
guarantee. If these constraints cannot be met, explain that browser-assisted export is
unavailable and retain the approved raw-preservation choice; do not claim automated export.

## CSV semantics and completeness

Matched [CSV exporter](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/export/password_csv_writer.cc)
uses columns `name,url,username,password,note`, UTF-8 strings, lexicographic row ordering,
and available affiliated-domain expansion.
The [quoting writer](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/export/csv_writer.cc)
quotes commas, quotes and either newline character, doubles interior quotes, and emits
CRLF on Windows versus LF elsewhere, with a trailing line ending. Fixtures have no BOM.

[Credential UI grouping](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/password_manager/core/browser/ui/credential_ui_entry.cc)
can combine credentials and concatenate distinct nonempty notes with newlines. A direct
row dump is therefore not automatically identical to the manual browser export. Preserve
eligible records/notes in the protected representation; implement tested browser-compatible
CSV projection rather than silently dropping notes or inventing affiliation data. Unsupported
encrypted notes or grouping requirements must be disclosed through the approved failure path.

## Raw-file preservation fallback

On portable-capture failure, explain the reason and offer explicit native preservation
or cancellation. Preserve exact available database bytes and applicable `-wal` or
`-journal` companions; inventory absence/presence and hashes. The `-shm` file is transient
coordination data, not a durable recovery requirement; if preserved for diagnostics it
must be labeled accordingly, not blindly restored as authoritative state.

Known candidate source names are `Login Data` and locally present `Login Data For Account`
with their SQLite companions; applicability must be checked for supported Brave builds.
If a source is changing, request close-and-retry before raw copying. Corrupt-but-readable
bytes may be preserved with a corruption warning; unreadable bytes cannot be promised
as preserved. Missing sources do not become empty collections. Never claim successful
portable extraction, recovery assurance or cross-machine usability for this fallback.

Do not automatically archive the full Local State file or OS key stores: OS keys were
excluded by the accepted contents scope. This limits potential recovery if the original
key context is later lost. Any extra key-metadata preservation needs explicit scope review.
Protect-passwords/encryption and save-consent requirements still apply. Canceling the
whole operation remains terminal; raw preservation requires its own explicit choice.

## Dependencies and implementation order

**Proposed dependency, not added:** Microsoft.Data.Sqlite, with a version appropriate
for .NET 10 and a verified native SQLite runtime. Its purpose is standard SQLite schema,
read-transaction and backup handling on Windows/Linux, rather than inventing a database
reader. [Microsoft's backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)
and [connection options](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
document these capabilities; VFS selection is available but does not itself supply the
write-prevention proof above. Exact package/native-version pins and distribution targets
must be checked when introducing the dependency. No package was installed here.

Next implementation checkpoints within the approved scope:

1. Bounded isolated SQLite capture and schema/credential/note classification, tested
   against frozen fixtures, with no credential reads during discovery/navigation. Prove
   a no-source-write strategy before enabling live capture; otherwise use close-and-retry.
2. Fixture-driven crypto decoding with provider context, then platform lookup/unlock
   adapters for demonstrated providers only. Windows/Linux native access remains separately
   testable; inaccessible providers retain explicit raw-preservation handling.
3. Protected payload/CSV contracts and the explicit fallback/consent model, including
   truthful counts and backward-compatible archive versioning. No format version changed here.
4. Wire reviewed controls only after those paths are tested; investigate Selenium as a
   separate versioned integration instead of silently adding it to protected capture.

## Fixtures and checks performed

`tests/Core.Tests/Fixtures/Passwords/` contains only synthetic data:

- Source-derived v43 schema with all upstream test-schema tables and no upstream sample rows;
  upstream license retained in `UPSTREAM-LICENSE.txt`.
- Independent Windows-GCM/Linux-CBC database and note vectors, with fixed test-only keys,
  nonces and credentials explicitly marked synthetic; native OpenSSL encryption/decryption
  verifies the vectors without using application code.
- Empty, deliberately unsupported-version and corrupt-file cases; these are distinct outcomes.
- CSV quoting/Unicode/multiline-note vectors for both OS line endings.
- Frozen database/WAL/shared-memory artifacts containing a newer WAL-only commit and
  the Linux read-only experiment report. Shared-memory bytes are platform/runtime evidence,
  not a portable database-format guarantee.
- Source evidence and SHA256 manifest. Fixed test keys are never production credentials.

Run `python3 tests/Core.Tests/Fixtures/Passwords/verify-fixtures.py`: **6 independent
research-fixture tests pass**. `generate-fixtures.py` regenerates and asserts native crypto,
CSV and transaction results using Python SQLite/OpenSSL only; WAL salts/layout can vary
between generations and hashes intentionally track the frozen generated artifacts.

Application C# code and packages are unchanged, so the previous 557 .NET test result was
not rerun or presented as new validation. Native OS unlocking, real Brave export, source-safe
live capture and any restore behavior are not established by these research fixtures.

## Canonical CSV note selection refinement

The pinned `StoredCredential::GetPasswordNote` selects only the empty-name canonical
note. [Researched note projection](passwords-csv-notes.md) implements group sorting/joining
and refuses nonempty named notes rather than violating the approved no-omission rule.
Protected portable payloads continue preserving every note separately.

## Desktop affiliation/grouping refinement

The pinned desktop presenter delegates grouping to PasswordsGrouper, which consumes
affiliation-service group information and PSL-extension merging. The
[verified-group projection contract](passwords-csv-projection.md) implements row grouping
and projection without inventing those inputs. Offline group-source loading remains
separate work; a per-origin fallback is not silently treated as equivalent.

## Offline cache lookup refinement

The pinned backend resolves grouping from cached schema-v7 data and explicitly returns
a singleton for an absent facet, before PSL/private-registry related-group merging.
The [bounded cache reader](passwords-affiliation-cache.md) implements that raw lookup;
source capture and merge semantics remain separate requirements for verified CSV groups.
