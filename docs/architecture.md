# Project boundaries and phase 2

The dependency direction is:

```text
UI  ──┐
      ├── Backup ── Core
CLI ──┘     └────── Platform
```

- **Core** contains platform-independent, browser-independent backup contracts (`BackupModel`,
  `BackupBookmark`, `BackupBookmarkType`) and archive-v1/v2 contracts, generic portable Settings payloads,
  IO/verification and cryptography. It has no project or package dependencies.
- **Backup** owns Brave installation definitions, profile discovery, appearance metadata,
  bookmark parsing/export, inspection and export destination policy. It references Core
  and Platform, never UI. Existing types retain the `BraveBackup.Core` namespace to avoid
  unnecessary source API changes; they now belong to the Backup assembly. Consumers of
  browser services must reference Backup. This is not binary assembly compatibility.
- **Platform** supplies host directories (including configured Documents), PATH, path comparison, registry reads,
  executable
  product metadata and bounded command execution. It contains no Brave formats or paths.
- **UI** and **CLI** consume Backup independently. UI-only avatars/presentation stay in UI.
- **Extensions/TabManagerPlus** is a dependency-free .NET 10 project containing a bounded
  offline storage reader. It currently has test consumers only; UI/Backup/CLI integration
  and decoded saved-window support remain pending. See [the reader checkpoint](tabmanagerplus-storage-reader.md).

`HostEnvironment` centralizes OS calls. Browser path conventions and launcher definitions
remain in Backup, where injected filesystem/registry/version delegates allow existing
Windows and Linux detection tests to run without modifying browser data. Registry reads
use the framework's Windows registry API instead of parsing `reg` command text.

## Export protection

`ExportDestinationGuard.Validate` returns a normalized output path or throws before writing.
It protects standard Browser/Origin channel roots, supplied custom roots, and directories
identified by existing `Local State`, `Preferences`, `Secure Preferences`, `Bookmarks` or
`Login Data` markers. UI supplies all detected roots and the selected profile; CLI supplies
the selected profile. Direct exporter calls also run the standard-root/marker checks.

Symbolic-link/reparse-point destinations or ancestors are rejected conservatively. Backups
must be written through an ordinary filesystem path outside browser data. An unmarked,
nonstandard custom root cannot be inferred: callers must pass it to the guard. This check
does not defend against another process maliciously replacing directories between checks.

Both synchronous and asynchronous bookmark exports serialize before opening the destination,
write a newly created temporary sibling, revalidate, and replace the destination only after
the write completes. Failed/cancelled writes remove their temporary file; an existing backup
is retained until replacement. Replacing an entry rather than truncating it also avoids
modifying a profile through an existing hard link. Atomic rename is not a promise of
power-loss durability. Restore remains unimplemented and is not authorized by export.

## Compatibility and dependencies

The existing bookmark-only JSON remains schema version 1. See [backup-format-v1.md](backup-format-v1.md).
Bookmarks-only `.bravebackup` creation and verification are implemented; see
[archive v1](archive-format-v1.md). The password-data exception remains a later feature.
Existing UI and CLI bookmark exports remain usable. Platform additionally owns private
backup file creation and opening completed destination folders.

No NuGet package was added. The existing xUnit Visual Studio adapter is pinned to 2.8.0,
the version already resolved in the baseline, to eliminate NU1603 fallback warnings.
Existing test project names are retained; Core.Tests currently includes historical
bookmark-service and UI tests as well as contract/dependency regression checks.

## Phase 2 validation (2026-11-16)

- Baseline: 84 tests passed before changes.
- After changes: 104 tests passed, zero failures; no build or package warnings.
  Command: `dotnet test BraveBackup.slnx --verbosity minimal -m:1 -nr:false -p:RestoreIgnoreFailedSources=true`.
- Independent CLI build succeeded with zero warnings/errors:
  `dotnet build src/CLI/CLI.csproj --no-restore --verbosity minimal -m:1 -nr:false`.
- New coverage includes the frozen version-1 fixture, profile/standard/custom-root
  rejection, normalized path traversal, sibling paths, symlinks/dangling links, hard-link
  replacement, temporary-file cleanup, cancellation, UI rejection and assembly boundaries.
  Platform checks cover literal command arguments, failed commands and timeout termination.
- Executed on Linux. Windows path-mapping tests run with controlled inputs; the newly
  added native Windows environment/registry test returns without native checks on Linux.
  A native Windows test run is still required before claiming Windows runtime validation.
- No real profile contents were modified. Existing discovery tests/UI-view-model tests
  may invoke read-only installation detection. No UI layout or deferred control was changed.

## Settings category increment

The Brave-specific seven-key allowlist, snapshot extraction and Settings policy verification
belong to Backup (`BraveSettingsBackup`). Core validates generic Settings payload shape
and archive-v1/v2 integrity without native preference names. Settings-only and mixed
archives use manifest v2; bookmark-only writes retain v1. See [Settings backup](settings-backup.md).

## Extensions category increment

Core validates browser-independent raw snapshot inventory and binary payload integrity
for manifest v3. Backup owns approved source paths, extension ID/manifest rules and
source-stability/stopped-browser policy. Platform owns native file-type inspection, safe
read flags and process observations. Old v1/v2 writes/reads stay compatible; see
[Extensions backup](extensions-backup.md). TabManagerPlus-specific parsing is still deferred.

## Sessions category increment

Core owns generic portable-session/native-inventory models and manifest-v4 integrity
validation. Backup owns pinned Chromium format-3 replay, source selection, bounded stable
read-only capture and native/portable consistency checks. Platform file safety is reused;
no native format decoder or OS API moved into Core. Earlier archive versions remain
compatible. See [Sessions backup](sessions-backup.md).
