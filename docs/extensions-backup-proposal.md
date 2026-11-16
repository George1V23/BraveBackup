# Extensions backup: scope checkpoint

Status: **all five choices approved with “keep all” and implemented**.
See [Extensions backup and archive v3](extensions-backup.md).
The previous Settings increment and the embedded Export view fix remain in place.
Extensions is next in [the agreed category sequence](backup-completion-plan.md).

## Current implementation and gap

`BraveProfileDetailsReader.GetExtensions` lists profile Extensions directories and reads
one manifest per extension. It picks a version directory alphabetically, which is not
reliable numeric version ordering or evidence of the active version. Missing manifests
fall back to directory names; localized names may remain message identifiers. The UI
already describes extension counts as discovered directories, not enabled extensions.

There is no extension backup writer, extension-data collector, process-state check or
extension restore workflow. Do not reuse the metadata reader as proof that all extension
files or settings were captured. The new collector should be strict without removing the
existing discovery fallback behavior.

## Proposed controls and scope

Keep the existing Extensions category checkbox. Its description and summary state exactly
what is included; no additional per-extension selector is proposed in this increment.
The following are separate review choices:

| Choice | Proposed behavior |
| --- | --- |
| Extension inventory | Record each discovered on-disk extension ID, available manifest name/version metadata and all observed version folders. Do not label a version active or an extension enabled without verified evidence. |
| Extension files | Save all regular files beneath each discovered `Extensions/<id>/<version>/` directory, preserving version directories and relative paths. Do not choose only the alphabetically latest folder. |
| Supported stored data | Save complete raw directory snapshots of `Local Extension Settings/<id>/` and `Sync Extension Settings/<id>/`, where present. Do not decode, merge or rewrite their databases. “Sync” here means locally stored files; no internet operation. |
| Capture condition | Require Brave instances to be closed for this Extensions operation. If the app cannot establish a stopped state, block with an explanation. Do not close Brave automatically or write lock files into profiles. |
| Unencrypted stored data | Keep archive encryption on by default. If the user explicitly turns it off, require a confirmation explaining that copied extension data may contain readable private information or authentication material. Do not extract passwords or create a plaintext sidecar. |

Missing storage directories are recorded as absent, not silently represented as captured.
An existing empty supported directory is recorded as empty. If an included file/directory
is unreadable, changes during capture, exceeds limits, or cannot be captured safely, fail
the entire requested backup rather than omit it. A profile with no discovered extensions
gets an explicit zero count; other selected categories still run normally.

The app never executes extension scripts, installs/uninstalls extensions, changes enabled
states, downloads packages or writes into browser profiles during backup. Saving files is
not a claim that Brave will accept or activate them when copied to another installation.

## Defined limits of this first Extensions slice

The stored-data choice covers exactly the two directories above. It does not include
IndexedDB, DOM Local Storage, Cache Storage, service-worker state, in-memory session storage,
policy-managed values or every extension-specific data source. It is not a complete
backup of every extension's settings. The UI must disclose that supported scope before
saving. Wider storage coverage needs its own research and review; no opaque catch-all
copy of the entire profile is proposed.

External unpacked extensions outside the selected profile and browser-bundled components
are outside this on-disk profile-directory slice. Do not follow arbitrary paths found in
preferences. A discovered extension directory with missing/invalid manifests fails this
strict backup scope, even though the existing read-only metadata UI may show a fallback.

TabManagerPlus remains a separate category and later project under Extensions. Generic
file snapshots may contain its installed files and supported raw storage like any other
extension; they do not claim to decode or restore its saved windows/groups. Do not silently
exclude its generic files, or mark the separate TabManagerPlus feature implemented.

## Technical approach after review

1. Pin official Chromium extension paths/storage sources and add synthetic fixtures.
   Document raw directory capture as such; do not invent a LevelDB or Brave data format.
2. Implement OS-specific process inspection in Platform and Brave matching/policy in
   Backup. Indeterminate process visibility fails closed for this operation. Repeat checks
   during capture and before publication, and reject source changes. Process observation
   is not an OS-level freeze; document that limitation and test a newly started browser.
3. Add a bounded, read-only collector for approved profile subtrees. Reject traversal,
   symbolic links/reparse points and special files. Do not expand the backup scope through
   links or preference paths. Stable read/hash checks and cancellation apply per file.
4. Stream arbitrary binary payloads and metadata into a new archive **manifest v3** with
   required `extensions-v1` capability. Include Bookmarks and/or Settings when selected.
   Preserve archive-v1/v2 read support and existing writes when Extensions is not selected.
   BBKENC1 envelope version and standalone bookmark JSON schema remain unchanged.
5. Validate declared inventory, exact file roles/paths/counts and lengths/hashes before
   publication. Retain verified publication, overwrite protection and private temporary
   files. Raw database bytes need binary-aware validation, not JSON parsing.
6. Report discovered extension/version counts, captured files/bytes and present/absent
   supported storage areas. Reports omit extension data values, source paths and secrets.
   UI descriptions distinguish archived files from installed/enabled/restorable extensions.

Proposed initial resource limits: 10,000 discovered extension IDs per profile; 256 MiB per
file; existing global 100,000 ZIP entries and 100 GiB archive limits. Keep metadata bounded
and fail explicitly at limits. Do not load whole extension trees into memory. No NuGet
package is proposed for raw snapshots; any later database reader needs a separate purpose
and dependency explanation.

## Evidence and uncertainty

- [Chrome extension Storage API](https://developer.chrome.com/docs/extensions/reference/api/storage)
  describes distinct local, sync, session and managed areas. Extension state is not limited
  to one generic settings JSON file; session storage is memory-backed.
- [Chromium extension directory definitions](https://github.com/chromium/chromium/blob/main/extensions/common/constants.h)
  identify the Local Extension Settings and Sync Extension Settings directories.
- [Chromium extension preferences](https://github.com/chromium/chromium/blob/main/extensions/browser/extension_prefs.cc)
  maintain browser-managed installation/version/state metadata. On-disk folder enumeration
  alone does not establish which version is active.
- [LevelDB documentation](https://github.com/google/leveldb/blob/main/doc/index.md)
  describes directory-based database contents, locks and API snapshots. An arbitrary
  live directory copy is not a database API snapshot. Requiring a stopped browser is a
  proposed application policy to avoid knowingly copying actively changing stores; it
  does not by itself prove semantic recoverability of a store.

These are primary upstream references inspected on 2026-11-16. Implementation should pin
the consulted revisions and document fixtures/limitations. No personal extension data was
read for this proposal, and no generic cross-version restore behavior has been established.

## Acceptance and next checkpoint

Test synthetic multi-version directories (including 9.x versus 10.x), localized metadata,
empty/missing storage, raw binary bytes, multiple installations/profiles, mixed categories,
both protection modes, all compression choices, process stopped/running/unknown/restarted,
links/special files, malformed paths/manifests, limits, cancellation, changed/unreadable
sources, verification failures and unchanged browser inputs. Frozen v1/v2 and existing
HTML/JSON tests must remain valid. Native Windows runtime verification is still a release
check on this Linux host.

Next checkpoint: review the implemented five choices before the next category.
Restore, Import, the Settings navigation page, Sessions, Passwords, Merge/Sync and the
TabManagerPlus-specific feature retain their separate reviewed increments.
