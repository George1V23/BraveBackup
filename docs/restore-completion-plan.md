# Restore phased completion plan

Status: sequence approved by the user. Phase 1 is implemented for review; see
[read-only inspection and validation](restore-phase1-preview.md). Phase 2 is now implemented
for review: [source selection, mapping and preflight](restore-phase2-planning.md).
Phase 4 is implemented with its UI: [bookmarks restore](restore-bookmarks.md) and [execution controls](restore-execution-controls-draft.md), pending your review. Phase 3 is in progress: [safety/journal contracts and synthetic transaction tests](restore-safety-v1.md).
Production storage/recovery integration and later phases remain pending.
The [fifteen reviewed decisions](restore-ui-review.md) define the required behavior.
Each phase ends with working evidence, tests, documentation and a user review before
starting the next phase. Existing backup/export functionality must remain operational.

## Existing implementation and gaps

- `BackupArchive.Validate` authenticates encrypted archives and validates structure,
  category contracts and hashes for formats 1–8. `BraveSettingsBackup.ValidateArchive`
  adds Brave-specific checks. Archive metadata already identifies source installations,
  profiles and category representations. These are reusable read-side components.
- Platform code supplies process observation, private-file creation and folder opening.
  Discovery supplies existing destination installation/profile metadata.
- The original Restore placeholder has been replaced with read-only inspection and
  source/target planning. There is still no destination writer, safety snapshot contract,
  modification journal or rollback engine.
- Backup support does not establish restore compatibility. Portable password payloads,
  raw password files and CSV are different representations; extension/session captures
  also require their own destination compatibility checks and writer research.
- Archive profile directory names are informational and must never determine destination
  paths. Existing profile selection in Backup must not become implicit Restore mapping.

## 1. Read-only source selection, unlock and preview

Implement the approved file picker/full path, masked unlock field/action and metadata
preview. Validate archives using the existing contracts; show creation time, versions,
source installation/profile/category inventory and password representation/limitations.
Perform validation off the UI thread, handle cancellation/stale asynchronous results,
and avoid plaintext extraction to disk. Clear source passwords and dispose owned decrypted
state when changing files/leaving the page. Show clear errors for corrupt, unsupported
or incorrectly unlocked archives without exposing secret/native exception contents.

Review evidence: synthetic plain/encrypted v1–v8 archives display correct inventories;
wrong passwords/tampered files fail; source switching clears stale data/secrets. No Brave
profile writes or restore action are enabled.

## 2. Source choices, target mapping and read-only restore plan

Add initially unselected source-profile/category checkboxes, reviewed selection shortcuts,
and independent mapping to detected existing destination profiles. Display full paths;
reject duplicate destinations and path aliases. New profiles are created through Brave
first. Add browser status/Recheck and the planned changes preview. Validate source/target
identity, path boundaries, version/category compatibility and access without modifying
profiles. Unsupported categories stay visible with reasons and cannot be selected as
restorable. Distinguish a proposed category from a tested, enabled restore capability.

Review evidence: multi-installation/profile selection and mapping work; no automatic
name matching, hidden omissions or writes occur; invalid/running/unknown destinations
block the plan. Final execution remains disabled until the safety engine is ready.

## 3. Safety backup, modification journal, pause/resume and rollback engine

Define/document a bounded encrypted safety snapshot and journal contract before emitting
it. Capture original bytes and original absence for every affected file, plus metadata
required by supported recovery. Preserve originals across pauses; never overwrite them
with already-restored data. Verify all required safety backups before the first destination
modification. Safety output must remain outside browser data and source archives; prevent
collisions between operation artifacts and destination files. Use separate masked safety
password/confirmation fields, with no automatic source-password reuse.

Build the transaction engine against synthetic directories first. Freeze the plan and
track intended/in-flight/completed modifications so rollback includes unfinished categories
and removes files originally absent. Revalidate sources, destinations and stopped-browser
state before writes/resumption; detect conflicting changes rather than overwriting them
silently. Use safe staging/publication boundaries and reject unsupported filesystem cases.
Document durability, metadata recovery and interruption limits; do not promise a cross-file
atomic transaction or restart-resume without demonstrated support.

Implement the reviewed state flow: Cancel requests a pause at a safe write boundary,
then prompts for rollback. Declining leaves the operation paused. Only **Resume restoration**
continues after revalidation. Rollback requires explicit approval, uses the original safety
snapshot and reports partial failure accurately. Closing/operation failure must preserve
recovery artifacts and never silently claim rollback or continue writes. No new visible
close/recovery choices are added without the user's control review.

Review evidence: injected write failures, cancellation boundaries, browser-state changes,
destination races, failed safety verification, pause/resume, successful rollback and partial
rollback failure are tested on synthetic data. Progress records real completed categories
under installations/profiles. No real-profile restore is enabled in this phase.

## 4. Complete bookmarks-only restoration end to end

Research/pin the native destination format and implement a tested writer from the archived
bookmark representations. Preserve unselected categories. Connect final review/confirmation,
encrypted safety backup, stopped-browser checks, progress, pause/rollback/resume, completion
report and Open safety-backup folder. Enable only tested bookmark compatibility combinations.
Keep credential contents out of reports; selection and preview remain write-free.

Review evidence: restore synthetic source bookmarks into a different synthetic destination,
verify native output and unchanged unrelated files, roll back to exact original bytes/absence,
and demonstrate pause/resume and failure recovery in the UI. Preserve all backup/export tests.

## 5. Enable remaining categories as separate reviewed increments

Complete these in order, with research/fixtures, writer/rollback tests, compatibility reasons
and an individual user checkpoint for each:

1. **Settings:** restore the supported stored preference choices while preserving unrelated
   preferences and categories sharing the destination file.
2. **Extensions:** establish destination registration/storage compatibility before claiming
   extensions are installed/restored; raw captured files alone are not sufficient evidence.
3. **Sessions:** distinguish portable session contents and optional native files; enable only
   tested destination formats and explain persisted-state limitations.
4. **TabManagerPlus:** implement tested restoration of supported saved windows within its
   extension project, preserving unrelated extension data.
5. **Passwords:** separately evaluate protected portable payloads, explicitly preserved raw
   files and CSV. Research destination encryption/key access and supported schema writes.
   Never assume raw files work across machines/providers, install OS keys from a backup,
   silently omit unsupported records or stage plaintext without the required explicit choice
   and fresh confirmation. Keep unsupported forms disabled with a truthful reason.

For every category, use the same safety engine and review confirmation; verify unrelated
data stays unchanged and rollback covers every modification. Update support descriptions
as categories become usable. Do not infer generic text/file merging from this phase.

## 6. Integration, lifecycle and platform validation

Verify combined multi-category/multi-profile restores, hierarchical completion results,
frozen selections, password clearing, cancellation during each stage, retained safety paths,
folder opening and reports. Exercise representative Windows/Linux behavior using synthetic
profiles; clearly identify platform checks that cannot be run in the current environment.
Test source/destination changes, unsafe paths, insufficient space/access, interrupted writes
and recovery failures. Keep unsupported states actionable and prevent misleading success.

Review evidence: full regression suite plus native UI smoke checks and a concise compatibility
matrix with remaining limitations. No real credential/profile modifications are needed for
automated validation.

## Later scope

Import, Backups management and other unreviewed pages keep their separate reviews.
The [future offline Diff/Merge tool](diff-merge-future.md), including KDiff3 evaluation,
remains within Profiles → Merge/Sync. It does not change replacement Restore behavior.
Explain any proposed new dependency before adding it; document any new safety/archive
format and preserve existing archive reads.
