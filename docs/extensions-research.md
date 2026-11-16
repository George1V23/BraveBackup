# Extension snapshot research and fixtures

Inspected 2026-11-16 for [the approved raw Extensions scope](extensions-backup.md).
The implementation copies opaque bytes; it does not decode or fabricate database records.

## Primary references

Chromium revision `24c43b5e3ea539d800ca5265218b7dad4fb669b4` was resolved through the
official GitHub API and the pinned raw files were fetched directly:

- [extensions/common/constants.h](https://github.com/chromium/chromium/blob/24c43b5e3ea539d800ca5265218b7dad4fb669b4/extensions/common/constants.h)
  defines manifest/locale names and Local Extension Settings / Sync Extension Settings
  directory names. Those source paths are the two supported storage subtrees.
- [extensions/browser/extension_prefs.cc](https://github.com/chromium/chromium/blob/24c43b5e3ea539d800ca5265218b7dad4fb669b4/extensions/browser/extension_prefs.cc)
  maintains browser-managed extension installation/version/state metadata. Folder
  enumeration alone is not used as evidence that an extension/version is enabled/active.

The attempted old `extensions/browser/api/storage/settings_frontend.cc` source path was
not available; no claim relies on having read that file.

[Chrome extension storage API](https://developer.chrome.com/docs/extensions/reference/api/storage)
distinguishes local, sync, session and managed storage. This justifies disclosing the
scope as two on-disk areas, not every possible data source or every effective setting.
Memory-backed session storage is not captured. No network sync is performed.

[Manifest version documentation](https://developer.chrome.com/docs/extensions/reference/manifest/version)
defines a package version as one to four numeric components, each 0–65535, without
nonzero leading-zero components, and not all zero. The reader uses these rules without
selecting a presumed active version. It retains raw localized manifest names rather than
executing an extension or inventing a translated label.

LevelDB revision `29465588d3dfdc80fba5e198037582b8fdb5dd60`:

- [doc/index.md](https://github.com/google/leveldb/blob/29465588d3dfdc80fba5e198037582b8fdb5dd60/doc/index.md)
  describes directory-based storage, locking and database API snapshots. A raw file-tree
  copy is not advertised as one of those API snapshots or as proof of recoverability.
  The app requires a stopped-process observation and rejects changed sources, while
  documenting the limits of process observation and filesystem races.

Linux source safety follows the documented fixed-width
[statx structure](https://man7.org/linux/man-pages/man2/statx.2.html) and
[read-only/nonblocking/no-follow open flags](https://man7.org/linux/man-pages/man2/open.2.html).
Native calls remain in Platform; Core does not contain Windows/Linux APIs.

## Fixture provenance and limits

`ExtensionBackupTests` creates synthetic profile trees with valid extension IDs, two
manifest versions (9.0 and 10.0), unresolved localized names, empty directories, local
binary markers and sync-area absence/emptiness. Another identity has retained storage
without installed files. The arbitrary binary markers are test files, not fabricated
LevelDB files or evidence that a database can be reopened after restore.

`tests/Core.Tests/Fixtures/Archive.v3.extensions.bravebackup` is a frozen independently
Python-generated ZIP using the application-owned inventory schema. Its generator is
`generate-extensions-v3-vector.py`. Adversarial tests recompute inventory hashes while
altering structure, references or metadata so validation is not merely a checksum test.
Prior v1/v2, bookmark JSON and encrypted envelope fixtures remain unchanged.

No personal extension data was used to derive the format or fixtures. No package was
executed, browser closed, native extension preference rewritten or install attempted.
Future restore, broader storage decoding and TabManagerPlus-specific saved-window support
need their own sources, fixtures, platform checks and user review.
