# Extensions backup and archive v3

Writes with Extensions and no Sessions retain v3. Adding Sessions now uses
[manifest v4](sessions-backup.md), preserving the Extensions behavior below.

All five [scope choices](extensions-backup-proposal.md) were kept by the user and are
implemented. Extensions can be selected alone or with Bookmarks and/or supported Settings.
This captures files, not an installed/enabled extension configuration or a restore workflow.

## Included files and inventory

For each selected profile, capture all discovered version directories under
`Extensions/<id>/<version>/`, and all regular files in the supported raw storage areas
`Local Extension Settings/<id>/` and `Sync Extension Settings/<id>/`.

Every discovered version is retained, including 9.x and 10.x simultaneously. No alphabetical
folder choice is used to infer an active version. Manifest name/version metadata is stored
per observed directory; localized `__MSG_...__` names remain unresolved metadata, and their
locale files are captured like other package files. Scripts are never executed.

The inventory includes IDs found only in supported storage, even if their package files
are absent. These are reported as storage-only identities, not installed extensions.
Per identity, each supported storage area is explicitly `absent`, `empty`, or `present`.
Empty directories are retained as inventory paths rather than ZIP directory entries.
A profile with no package or supported storage identities has an explicit zero inventory.

Only raw bytes and directory structure are captured, not native file permissions, owners
or a database's logical records. The collector never opens LevelDB through a database API,
creates source lock files, modifies databases or rewrites extension preferences. In-memory
working buffers are bounded and cleared; whole file trees are not loaded as byte arrays.

## Limits of this slice

IndexedDB, DOM Local Storage, Cache Storage, service-worker state, in-memory session
storage, managed policy, external unpacked paths and browser-bundled components outside
these profile subtrees are not included. The UI discloses local/sync-only storage coverage.
This is not a complete backup of every extension's settings or proof of recoverability.

Missing/invalid manifests, unreadable files, source changes, links, special files and
resource-limit violations fail the whole requested archive; no included item is silently
omitted. Existing display-only discovery still permits its old metadata fallbacks and
counts discovered extension directories; the strict backup collector is separate.

Generic snapshots can include TabManagerPlus package files and its supported raw storage.
They do not implement its saved-window/group parser or restoration. That separately
reviewed category still requires its own project under Extensions.

## Browser-closed policy and file safety

Extensions requires a stopped-browser observation before any output, after confirmation,
during collection/copying and before publication. Observed running or indeterminate state
blocks the operation; the app never terminates a browser. A failed process observer is
indeterminate. Bookmarks/Settings operations without Extensions keep their previous policy.

Platform observes visible process names with caller-supplied Brave/executable-name stems.
Matching uses name boundaries (`brave`, `brave.exe`, `brave-*`, `brave_*`), avoiding
`BraveBackup`/`bravery` false matches. The backup process itself is excluded. A complete
no-match scan of visible names produces Stopped; a matching name produces Running;
unreadable process names that cannot be explained by exit produce Unknown.

This observes the current process namespace/permissions; it is not a system freeze or
proof about invisible processes on another host/namespace. Short-lived restarts between
observations cannot all be detected. Source-tree shape, sizes, mtimes and hashes are
therefore checked again before publication. Same-size content changes with restored
mtime are detected by rehashing. These checks still do not promise protection from a
hostile last-instant filesystem swap or a transaction across multiple profiles.

Linux uses `statx` to classify objects without following the final link and read-only
`open` with no-follow/nonblocking/close-on-exec flags. It checks the opened descriptor's
regular-file type, avoiding FIFO waits and source creation. Windows rejects reparse/device
entries, opens read-only without writer sharing and rechecks object type. Directory
ancestors are checked during traversal/opening. Unsafe or case-colliding relative paths
are rejected, not followed or renamed silently.

## Protection and user feedback

Encryption remains checked by default. For an explicitly unencrypted Extensions archive,
a fresh confirmation explains that raw extension data may contain readable private
information or authentication material, and shows the exact destination. Missing callback,
Cancel or window dismissal declines saving. The request is frozen; a changed destination,
scope or new attempt cannot reuse old consent. The workflow does not extract passwords,
decrypt credentials or create plaintext sidecars.

Existing overwrite confirmation, cancellation, private temporary output, verification and
same-directory publication remain in effect. The collector never writes into browser
profiles. Raw file hashes/source checks are completed before successful publication.

Completion/report include package-identity count, observed version count, storage-only
identity count, raw file/byte totals and present/empty/absent supported storage counts.
No extension IDs, names, file contents, source paths, credentials or raw exceptions are
included in the report. Completion clearly states that extensions were not installed or
restored. Stage/profile progress is actual; copying/verification uses indeterminate progress
where no reliable aggregate percentage exists.

## Manifest v3 contract

Archives containing Extensions use `formatVersion: 3` and required `extensions-v1`.
Capabilities are the selected subset of the canonical order
`bookmarks-v1`, `settings-v1`, `extensions-v1`; Extensions must be present for v3.
Every profile contains exactly each requested category. Without Extensions, writers keep
v1 for Bookmarks-only and v2 for Settings-containing backups. Current validation reads all
three versions. Older readers reject unsupported required versions instead of skipping data.
BBKENC1 envelope version 1, existing bookmark JSON schema 1 and HTML export are unchanged.

Extensions category version 1 declares one `extension-inventory` JSON payload and zero or
more `extension-file` binary payloads, each with uncompressed length and SHA-256:

```text
installations/<installation-id>/profiles/<profile-id>/extensions/inventory-v1.json
installations/<installation-id>/profiles/<profile-id>/extensions/data/<extension-id>/<area>/<relative-path>
```

Areas are `package`, `local`, `sync`. A package relative path includes the version folder.
Inventory schema fields:

- `schemaVersion: 1`, `extensions: [...]`;
- per identity: `id`, `versions`, `localStorage`, `syncStorage`, `directories`, `files`;
- per observed version: `directory`, `version`, `name`;
- directories: canonical area-relative paths, including empty directories;
- per file: `area`, `relativePath`, `archivePath`, `length`, `sha256`.

Core checks unique identities/versions/paths, explicit storage states, directory-parent
closure, manifest-file presence, exact inventory-to-manifest linkage, declared file hashes,
canonical paths and no extra payloads. Binary files are hashed as bytes, not parsed as JSON.
Backup additionally checks Chromium extension IDs and that observed metadata matches the
archived manifest bytes. The application-owned inventory does not invent a native browser
or LevelDB format. No files are extracted during validation.

## Resource bounds

- 10,000 package/storage identities per profile.
- 256 MiB per raw file; 1 MiB per extension manifest.
- 16 MiB inventory payload and version-metadata text budget.
- 100,000 observed source paths across the operation (including empty directories/absent
  root markers), and the existing global ZIP-entry and archive-size limits.
- Maximum 64 path components, 768 characters per relative source path and 1,024 UTF-8
  bytes per complete ZIP entry name. Nonportable punctuation, trailing dots/spaces,
  traversal and case collisions fail explicitly.

Raw copying uses 64 KiB buffers; encrypted ZIP streaming/verification still avoids plaintext
staging files. Inventory/source metadata is bounded separately from file bytes.

## Code and test responsibilities

- Core `ExtensionSnapshotInventory`/item/version/file records define browser-independent
  snapshot metadata. `ExtensionSnapshotFormat.SafePath`, `ValidateInventory` and
  `ValidateCategory` check paths, identity/file linkage and binary integrity.
- Backup `BraveExtensionBackup.ReadManifest` and `ValidateCategory` enforce browser-specific
  ID/version/manifest rules. Internal `ExtensionCaptureSession` scans, streams, checks
  stopped state and rehashes sources. `ExtensionBackupOptions` accepts a host confirmation
  and optional trusted-host test observer; production UI uses the actual process probe.
- `BraveArchiveBackup.CreateAsync` retains its existing overload and adds an overload with
  explicit extension options. `BackupResult.ExtensionsSummary` carries safe counts.
  Existing `BraveSettingsBackup.ValidateArchive` now applies both Settings and Extensions
  browser-specific policy after generic Core validation.
- Platform `ProcessPresenceProbe.Observe`/`Assess` isolate host process observation;
  `SnapshotFileAccess.Kind`/`OpenRead` isolate native object classification/open flags.
- UI connects selection, the per-operation warning, safe feedback and disposal cleanup.
  No NuGet package, restore behavior or new CLI command was added.

All 351 test cases pass on Linux: 284 Core, 48 Brave, 13 Linux and 6 Windows-labelled
cases. Coverage includes both protection modes/four compression choices, mixed categories,
multiple scopes, 9.x/10.x versions, localized names, raw binary data, storage-only/empty/
absent cases, strict v3 inventory/linkage, manifest mismatches, old v1/v2 fixtures,
source changes/links/size limits, cancellation, process restart/unknown/failure, fresh
unencrypted consent and UI feedback. Native Linux checks exercise an isolated child process
and regular/link/FIFO objects. Existing export/Settings/crypto regressions remain passing.

A Linux desktop fixture exercised the actual Extensions checkbox and Create Backup button,
verified a combined encrypted v3 output, and cancelled the actual unencrypted-data warning
with no output. Its process observer was explicitly injected for synthetic browser data;
it did not close or back up a real running Brave instance. [Review screenshot](screenshots/extensions-completion.png).

[Research and fixture provenance](extensions-research.md) document the pinned sources and
independent Python archive-v3 fixture. Native Windows execution and independent encryption
review remain release checks. The next user checkpoint is review of this increment before
Sessions/native sessions or another category starts.
