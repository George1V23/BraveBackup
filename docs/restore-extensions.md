# Extension compatibility in Restore

Open and validate a backup containing Extensions. Select an archived profile, map it
to an existing destination profile, close Brave and choose **Recheck**. A Linux
destination with the same recorded Brave product and version can compare archived
packages with the destination's registration records. Extensions remains unavailable
for writing at this checkpoint.

The result distinguishes a matching registered package, a registered version missing
from the archive, different files at the same version, missing or unsupported
registration, and storage-only identities. The count covers all archived identities.
The first 50 identities include individual results and the archive's Local and Sync
states. The results describe the inspected archive and the destination at the time of
comparison. Run Recheck again after changing the mapping or destination.

## What the comparison verifies

- The archive inventory and every referenced file pass the existing integrity checks.
  The application retains extension IDs, version numbers, package fingerprints and
  storage presence. It does not load extension code or unpack database contents.
- The destination has one readable registration for the identity under
  `extensions.settings` in `Preferences` or `Secure Preferences`. The accepted
  record is an internal packaged install and points to a relative
  `<id>/<version-directory>` inside the profile's `Extensions` directory. Conflicting
  records, links and unsupported locations cannot produce a match.
- The registered version's relative file paths and SHA-256 hashes match one archived
  package for that version. The install-directory suffix may differ. An unregistered
  package directory alone has no effect on the result. Package metadata files are
  included in the comparison. Empty directories are outside the fingerprint.
- The comparison observes stopped-browser state before and after checking. It bounds
  destination traversal to 100,000 paths and 512 MiB of hashing per profile. Leaves
  above the archive's 256 MiB per-file limit also prevent a match. The compact
  archive inventory has a 100,000-entry budget across identities and package versions.
  Archive inspection still works when a profile exceeds the comparison budget.

A matching package records evidence about the on-disk files and registration. Effective
enablement, signed install policy, and integrity enforcement are handled by Brave.
The report labels Local and Sync as facts from the **archive**, separate from the
registration result. A raw storage snapshot is neither a checked LevelDB image nor
proof that an extension can use it.

## Sources and evidence

- Chromium reads the registered version, relative package path and installation
  location from its extension preference records. The persisted internal install
  location is defined in its manifest schema. [Chromium extension preferences](https://chromium.googlesource.com/chromium/src/+/234f53daea569f56d53d88d3d143a8c9bbdaeec9/extensions/browser/extension_prefs.cc#1460),
  [Chromium manifest location](https://chromium.googlesource.com/chromium/src/+/84fa9085bed1e927afa7c952ee0d8d5d636be37e/extensions/common/mojom/manifest.mojom).
- Chromium exposes local and sync storage as separate extension API areas. The
  backup format and opaque storage constraints are described in the existing
  research. [Chrome storage API](https://developer.chrome.com/docs/extensions/reference/api/storage),
  [backup format research](extensions-research.md).
- Integration tests generate encrypted archives with multiple observed versions,
  same-version changes, unregistered package directories, storage-only data,
  conflicting registration and a linked destination package. They exercise the
  real Restore recheck workflow and assert that the archive and destination bytes
  stay unchanged. The Avalonia smoke test exercises the visible Recheck control.
- A separate local check used two disposable Linux profiles and the same unpacked
  synthetic extension in Brave 1.96.61 (Chromium 154). The extension wrote through
  `chrome.storage.local` in the first profile. After both browser processes closed,
  a complete copy of that store reopened in the second profile and returned the
  original value. This verifies the tested local storage case independently of the
  app's read-only comparison. It covers one extension, one local database, and one
  Brave build.

An automatic storage restore needs a guarded whole-directory replacement and
rollback from the verified encrypted safety snapshot. Publishing database files one
at a time through the file restore engine could leave a partial store at a pause or
failure boundary. Storage replacement, Sync handling, and extension installation
remain pending.
