# Proposed .bravebackup archive contract

Status: **proposal for user review; not implemented or frozen**. This is checkpoint 2
of [the completion plan](backup-completion-plan.md). Approval covers the contract and
bookmarks-only implementation increment, not restore or the remaining category readers.
Existing HTML/JSON exports and their public APIs remain unchanged.

## 1. User-visible behavior

| Choice | Proposed behavior |
| --- | --- |
| Backup scope | One archive containing all selected profiles from the active installation, or checked installations in Export All. |
| First supported category | Bookmarks. Selecting any other category blocks creation with an explanation; nothing selected is silently skipped. |
| Encryption | On by default; encrypt the entire ZIP, including manifest, profile names and entry names. Password and confirmation must match. |
| Unencrypted backup | Explicitly turning encryption off produces a normal ZIP with a `.bravebackup` extension; its contents are readable with ordinary ZIP tools. |
| Compression | None / Fast / Balanced / High map to .NET NoCompression / Fastest / Optimal / SmallestSize. Size savings are not guaranteed. |
| Existing destination | Ask before replacement. A failed or cancelled operation preserves the old file. A file that appears/changes after confirmation requires confirmation again. |
| Success | Report success only after finalization, validation and publication of the complete file. Offer Open destination folder and an in-app operation report. |
| Cancellation | Cancel before publication; discard temporary output. Once publication begins, finish the short commit and report its actual outcome. |
| Browser running | Bookmark capture is read-only, allowed if a coherent snapshot can be read; it is not a promise of a browser-wide transactional snapshot. Restore remains forbidden while its target profile is running. |

A nonempty encryption password is mandatory; do not silently trim, truncate or normalize
it. Keep the existing strength estimate advisory. Explain that a lost password cannot be
recovered. No password hints or password-recovery mechanism are stored.

## 2. Container and compatibility

Two unambiguous encodings share the extension:

- **Unencrypted:** an ordinary ZIP containing the manifest and payloads below.
- **Encrypted:** a `BBKENC1\n` binary envelope containing the encrypted ZIP stream.

Detect by bytes and validate the manifest, never just by the filename extension. The
manifest identifies `format: "BraveBackupArchive"`, `formatVersion: 1`. Envelope version
is separate from manifest version and from the existing bookmark `SchemaVersion: 1`.
This introduces the first archive version; it does not increment or redefine legacy JSON.
An arbitrary ZIP is not a valid BraveBackup archive merely because its extension matches.

A future reader must continue reading archive v1 and the existing standalone schema-1
bookmark JSON. A future Import review defines how to expose legacy JSON/HTML import in
the UI; the proposal does not claim an import workflow exists today. Unknown archive
versions, encryption algorithms or required category versions fail clearly before any
profile write. Optional informational JSON fields can be ignored; unknown required
capabilities cannot. Do not silently downgrade encryption or ignore required payloads.

## 3. Manifest, identity and bookmark payloads

ZIP entry paths use generated archive-local UUIDs, not profile names, executable paths
or user-provided directory names:

```text
manifest.json
installations/<installation-id>/profiles/<profile-id>/bookmarks/source.json
installations/<installation-id>/profiles/<profile-id>/bookmarks/portable-v1.json
```

`source.json` is the exact byte snapshot of that profile's Bookmarks file.
`portable-v1.json` is the existing schema-1 representation generated from the same snapshot.
Keeping both preserves native bookmark metadata without changing legacy JSON semantics.
The portable representation retains names, hierarchy, URL strings and order; it omits
native GUIDs, identifiers and timestamps as documented in [schema 1](backup-format-v1.md).
Native bytes are for later explicitly reviewed restore handling, not a guarantee that
blind replacement is safe across Brave versions. No favicon database is included.

Proposed manifest fields (UTF-8, camelCase; UUID strings, UTC ISO-8601 timestamps):

- `format`, `formatVersion`, `archiveId`, `createdUtc`, `applicationVersion` (actual build);
- `requiredCapabilities: ["bookmarks-v1"]`;
- `compression`: one of `none`, `fast`, `balanced`, `high`;
- `installations`: `{ id, product, channel, browserVersion, profiles }`;
- each profile: `{ id, displayName, directoryName, categories }`;
- each category: `{ id: "bookmarks", version: 1, files }`;
- each file: `{ path, role, length, sha256 }`, with roles `native-source` and `portable-v1`,
  byte length of uncompressed content and lowercase hexadecimal SHA-256 of those bytes.

Archive-local IDs distinguish installations/profiles even if names match. Product/channel
and version are labels, not restore destinations. Store only the profile directory's final
component as informational metadata; omit original absolute home, data and executable
paths. The user must explicitly map source profiles to targets in the later Restore review.
No machine fingerprint or cross-backup profile identity is introduced.

The manifest is written last from completed payload records. Every non-directory payload
entry must be referenced exactly once; reject duplicate names, duplicate IDs, missing or
unexpected payloads, invalid lengths/hashes and unsupported required capabilities. The
manifest itself is not included in its file hash list. In encrypted mode its bytes are
covered by the envelope authentication, like all ZIP metadata.

## 4. Encryption proposal

Use .NET 10 built-in cryptography and ZIP APIs; **no additional NuGet packages proposed**.
AES-256-GCM authenticates bounded chunks; PBKDF2-HMAC-SHA-256 derives a 32-byte key
from the exact UTF-8 password with a fresh 16-byte cryptographically random salt.
The proposed v1 work factor is 600,000 iterations, fixed and validated for this version.
It follows OWASP's PBKDF2 baseline; this is a compatibility/dependency choice, not a claim
that PBKDF2 is preferable to Argon2id for every use case. Verify latency on target hosts.

This is an application-specific envelope around standard cryptographic primitives. It is
not an existing encrypted-ZIP standard or an independently audited protocol. Before
publishing encrypted archives, freeze its byte specification and test vectors, review its
nonce, framing and truncation handling, and pass the adversarial tests below. A different
established encryption container can be proposed if that tradeoff is preferred.

### Proposed envelope bytes

All integers are unsigned little-endian unless explicitly stated. Fixed header is 48 bytes:

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 8 | ASCII `BBKENC1` followed by LF (`0A`). |
| 8 | 2 | Envelope version = 1. |
| 10 | 1 | Cipher = 1 (AES-256-GCM). |
| 11 | 1 | KDF = 1 (PBKDF2-HMAC-SHA-256). |
| 12 | 4 | Iterations = 600000. |
| 16 | 4 | Maximum plaintext chunk size = 1048576 bytes (1 MiB). |
| 20 | 16 | Random salt. |
| 36 | 8 | Random nonce prefix. |
| 44 | 4 | Reserved, all zero. |

A record is `length:u32 || ciphertext:length bytes || authenticationTag:16 bytes`.
The reader assigns sequential indexes starting at zero; indexes are not supplied by the
file. The 12-byte nonce is `prefix:8 bytes || index:u32 big-endian`. Associated data is
`rawHeader:48 bytes || index:u32 little-endian || length:u32 little-endian`.
A key is derived afresh per archive; never reuse salt/prefix deliberately, and never
restart a failed/cancelled archive with its old header/key. Never wrap the index counter.

Every non-final record has 1..1048576 plaintext bytes; all except the last data record
must be full-sized. After the final data record, require a separately authenticated
zero-length terminal record using the next index. EOF must immediately follow its tag.
Empty encrypted streams still require the terminal record, although an empty ZIP payload
is not a valid backup. Reject missing terminal records, trailing bytes, reordering,
truncation, oversized lengths, short reads and malformed headers. Authentication must
cover the entire stream including its terminal record before accepting the archive.

Proposed initial resource limits are 100 GiB of ZIP stream and 100 GiB total uncompressed
payload, at most 100,000 ZIP entries, a 16 MiB manifest, and a 256 MiB bookmark source
snapshot per profile. These are explicit v1 implementation limits, not browser-format
limits. Exceeding one fails with an actionable message, never partial success. They keep
chunk count far below nonce exhaustion. Reader bounds must be checked before allocating.

Compress before encryption. Stream ZIP output directly into the encrypting stream and
then a temporary sibling file: **no plaintext ZIP staging file** in encrypted mode.
Clear owned key/password-byte buffers and sensitive working buffers in finally blocks.
Immutable UI strings cannot be promised securely erased; never persist them or log them.
Only framing, KDF parameters, salt, nonce prefix and approximate encrypted size are public.

For unencrypted ZIP, payload SHA-256 and structural validation detect accidental corruption;
they do not prove authenticity against an attacker who can rewrite the manifest too.
For encrypted archives, wrong passwords or damaged authenticated data produce a combined
"Password is incorrect or the backup is damaged" error without dumping contents.

## 5. Capture, safe publication and operation feedback

1. Freeze installation/profile/category choices for this operation. Validate supported
   categories, selected profiles, encryption inputs and a non-profile destination using
   the existing destination guard with all discovered roots and selected profile paths.
2. Obtain overwrite confirmation if needed; recheck before publication. Reserve a new
   temporary sibling with exclusive create. Use private creation permissions through
   Platform on supported hosts; do not change the parent directory's permissions.
3. Read each bookmark file once into a bounded snapshot, parse that snapshot, and generate
   the portable payload from it. Retry a transient read/parse conflict a bounded number
   of times; fail with a profile-specific UI explanation if it remains unreadable.
   No synthetic empty bookmarks when a source file is missing, inaccessible or malformed.
4. Write entries and manifest, close ZIP, finalize encryption, flush and close output.
   Cancellation/failure removes incomplete output; report cleanup failures explicitly.
5. Reopen and validate the complete envelope/manifest/payload hashes without extracting
   into browser data. Encrypted validation uses the password only in memory. Decrypted
   ZIP may require seeking: use a bounded-memory virtual stream over authenticated chunks,
   not a plaintext disk staging file. Verify terminal authentication before trusting it.
6. Revalidate destination and overwrite state, check cancellation, then publish by a
   same-directory rename/replace. Preserve existing output until commit. Do not advertise
   atomic power-loss durability or protection from a hostile concurrent directory swap.

The writer must fail closed if its actual implementation cannot satisfy encrypted
verification without plaintext staging; revise the proposal rather than silently relaxing it.
A successful read-only capture can be made while Brave is open but may miss changes made
after the snapshot. Across profiles there is no shared transaction; do not imply one.

Show stages Validating / Reading / Writing / Verifying / Finalizing, with per-profile
counts and byte progress where measured. Use indeterminate progress for work without a
reliable total. Reaching 100% means successful publication, not merely copying the last
payload. The approximate size before writing is labelled as an estimate or Unavailable;
the final report gives the actual resulting file size.

The in-app report contains archive ID, UTC start/end, status, requested/completed category
counts, profile counts, byte counts and sanitized error codes. Do not include bookmark
URLs/titles, profile display names, absolute source paths, credentials, passwords, keys,
or raw exceptions. The destination path remains visible in the completion UI; omit it
from the report. No automatic sidecar report, telemetry or secret-bearing logs. Opening
the destination folder goes through Platform; cancelling that action leaves the archive.

## 6. Passwords and other future categories

The first writer accepts Bookmarks only. Password CSV, sessions, native sessions,
extensions/settings and TabManagerPlus are later increments with their own research,
fixtures, capability disclosures and tests. TabManagerPlus stays in its own project
under Extensions. Do not infer credential portability by copying an encrypted database.

When credentials are implemented, protection remains the default. Plaintext CSV must be
an explicit separate user choice, followed by a confirmation before the local save,
even when the enclosing archive is encrypted. The warning explains that extracted CSV
is readable by anyone with file access; for an unencrypted archive the warning also
states that no decryption password is needed. No plaintext sidecar is created implicitly.
A changed scope, destination, protection mode or new operation invalidates confirmation.
Unsupported password extraction blocks that selected category rather than substituting
an unusable database or silently excluding it.

The backup-password UI and manifest do not define how restored credentials, native
sessions or extension databases are installed. Restore still needs its separate review,
running-profile checks and a safety backup before any target files change.

## 7. Architecture and implementation acceptance

- Core: browser-independent manifest/envelope contracts, archive IO/validation and
  cryptography; no host-specific APIs or UI dependencies.
- Backup: Brave bookmark snapshot collection, category capability checks and destination
  policy using existing readers/exporters. No direct restore side effects.
- Platform: private temporary-file creation/permissions and opening folders.
- UI: immutable operation request, required confirmations, progress, cancel and outcomes.
  CLI stays independent of UI and can consume the same archive service without changes
  to its existing JSON export behavior. New CLI commands need a separate interface review.

Before enabling Create Backup, require:

- Round trips for encrypted/unencrypted archives, every compression choice, multiple
  installations/profiles with matching names, native bytes and legacy schema-1 values.
- Golden v1 archive fixtures and fixed deterministic cryptographic vectors (test-only
  randomness injection); production encryption always uses fresh secure randomness.
- Wrong password; mutated header/ciphertext/tag; truncated/reordered/duplicated records;
  missing terminal record; trailing bytes; malicious lengths/iteration values; limits.
- ZIP traversal/rooted paths/backslashes, duplicate entries/IDs, unknown required versions,
  undeclared/missing entries, invalid JSON and lengths/hashes, oversized decompression.
  No blanket extraction API for untrusted data; no writes to profiles during verification.
- Unsupported selected category, missing/invalid/empty-valid bookmark file, read failure,
  changed source, invalid destination, overwrite declined/raced, cancellation at each stage,
  failed verification, disk write failure and cleanup failure. No misleading success.
- Existing HTML/JSON tests and frozen schema-1 fixture still pass. Profile inputs and an
  existing destination remain unchanged after failed/cancelled operations.
- Native Windows and Linux filesystem/cryptography runs before claiming both are verified.

## Sources and design status

These sources document the primitives and risks; the proposed manifest, envelope and
limits are application design decisions, not Brave/Chromium formats or source-endorsed
protocols:

- [Microsoft: AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-10.0)
  and [Encrypt nonce uniqueness](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0).
- [Microsoft: PBKDF2 API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2?view=net-10.0).
- [OWASP: password storage and PBKDF2 work factors](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).
- [Microsoft: compression choices](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.compressionlevel)
  and [ZIP/TAR safety](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices).

Next checkpoint: approve or edit this proposal, then implement bookmarks-only archive
creation with tests and documentation. No archive-writing code was added in this review.
