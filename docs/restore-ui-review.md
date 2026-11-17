# Restore control review

The control-by-control review is complete. These decisions define the proposed behavior;
the phased implementation plan is awaiting approval before application code changes.

## Accepted controls

1. **Backup file selector — keep.** Browse selects a `.bravebackup` file and displays
   its full path. Selecting inspects the archive only; it does not modify profiles.
   Decryption and restore destinations are separate decisions.
2. **Encrypted backup password — keep.** An encrypted archive shows a masked password
   field and **Unlock backup** button. Unlock validates and reads the archive for preview
   without modifying profiles. The password is never saved or logged and is cleared
   when switching files or leaving Restore.

3. **Backup contents preview — keep.** After validation/unlock, show the
   archive creation time, recorded application/browser versions, installations, profiles
   and included data categories. Show portable/raw/CSV password representation and
   relevant compatibility limitations without credential values. This is read-only;
   restore source/target choices are reviewed separately.

4. **Backed-up profile selection — keep.** Show checkboxes for archived
   profiles grouped by archived installation, plus Select all/Clear all. Start with
   no profile selected, and keep these choices independent from current live-profile
   selection. Choosing source profiles performs no restore or destination selection.

5. **Restore data-category selection — keep.** For each selected archived
   profile, show the categories actually present in its backup with individual checkboxes.
   Start with none selected; provide Select all supported/Clear all. Unsupported restore
   categories remain visible but disabled with a reason. Each profile has its own choices;
   selecting a category changes no browser files.

6. **Destination profile mapping — keep.** For each selected archived profile,
   choose a detected Brave installation and an existing profile within it. Display the
   destination's full profile-directory path. Start without an automatically chosen target;
   do not infer identity from matching names or allow multiple sources to target the same
   profile in one operation. Choosing a target performs no write. Creating new destination
   profiles and merge behavior require separate review.

7. **Restore behavior — keep.** Restore replaces the corresponding destination
   data for selected categories only; unselected categories remain unchanged. Show this
   behavior explicitly before confirmation. Do not automatically merge or deduplicate data;
   merge/sync retains its separately deferred review within Profiles. Choosing this behavior
   performs no write. Unsupported category/target combinations must block rather than
   overwrite unrelated data. Mandatory safety-backup and final-confirmation controls are
   reviewed separately.

8. **Mandatory safety backup — keep.** Before modifying any destination,
   save the original data that the restore will change. Display its full destination
   path with Browse to choose a folder outside browser data. It cannot be disabled;
   creation/verification failure blocks restore. Preserve raw original bytes needed to
   undo changes, without claiming portable password recovery. Protect sensitive safety
   data with encryption; password entry/reuse policy is a separate review. No safety
   backup is written merely by selecting its folder. Exact rollback coverage and limits
   require implementation research, tests and documentation.

9. **Safety-backup encryption password — keep.** Provide separate masked
   Password and Confirm password fields for the encrypted safety backup, including when
   the source archive is unencrypted. Do not automatically reuse the source archive's
   unlock password. Require nonempty matching values before restore; never save or log
   them. Clear these fields when switching source files, leaving Restore, or completing,
   cancelling or failing an operation. Explain that a lost safety-backup password cannot
   be recovered by the application.

10. **Destination browser status and recheck — keep.** Show Closed, Running
    or Unable to verify for each destination installation, with a Recheck button. Running
    or uncertain status blocks restore. Ask the user to close Brave themselves; do not
    forcibly terminate it. Recheck immediately before safety capture and destination
    modification, and stop further writes if an unsafe state is detected during restore.
    Rechecking performs no profile write. Process-check limitations and partial-operation
    recovery must be documented; a status check is not a guarantee against a concurrent
    browser launch.

11. **Final restore review and confirmation — keep.** A Review restore
    action shows the exact source archive path, selected source profiles/categories,
    destination installation/profile paths, replacement behavior and encrypted safety
    backup location. Surface applicable compatibility or raw-password limitations without
    credential values. Offer Cancel and Confirm restore. Reviewing performs no write;
    explicit confirmation authorizes the frozen operation, which must revalidate sources,
    destinations and browser state and create/verify safety backups before modification.
    Material scope changes require a new review and confirmation.

12. **Restore progress and cancellation — edited by user.** Show a progress bar and
    a hierarchical completion list identifying Brave installations, profiles and the
    categories within each profile whose restoration has finished. Report actual progress;
    use an indeterminate bar while a meaningful percentage is unavailable. Freeze source,
    selections and destination controls while running. Cancel restore requests a pause:
    stop scheduling further restore work and finish any in-flight atomic write at a safe
    boundary before pausing. Then ask whether the user wants to roll back changes already
    made by this restore operation, using its verified safety backups. Do not automatically
    roll back or continue while this decision is pending. Track all modifications, including
    an unfinished category, rather than rolling back only items marked complete. Preserve
    safety backups and report rollback failures honestly. Declining rollback leaves the
    operation paused; it does not automatically resume or end the restore. Show a **Resume restoration** button. Only
    pressing it resumes the remaining work, after
    revalidating the frozen source, destinations and stopped-browser state. If those
    checks fail, remain paused and explain the issue rather than writing. Preserve the
    original safety backups across pauses/resumption; do not replace them with backups
    of already-restored data.

13. **Rollback progress and outcome — keep.** After explicit rollback
    approval, show a progress bar and per-installation/profile/category recovery results.
    Restore the pre-operation state of all data this operation changed, including any
    unfinished category. Keep destination browsers closed and retain verified safety
    backups. Do not label rollback complete unless all required recovery succeeds; show
    unresolved targets and safety-backup paths on failure. No automatic restore resumption
    follows rollback.

14. **Completion and recovery report — keep.** Show the actual final outcome (restored, rolled back, or
    failed/partially recovered), with results grouped by Brave
    installation, profile and category. Show the retained safety-backup paths and an
    Open safety-backup folder action. Offer a detailed report containing stages, counts
    and sanitized error codes without credentials or encryption secrets. Do not report
    paused work as finished or failed recovery as successful. Opening a folder performs
    no restore or rollback action.

15. **New destination profiles — keep.** Initially restore into existing
    detected profiles only. To use a new profile, the user creates it through Brave,
    closes Brave and refreshes discovery in BraveBackup before mapping the destination.
    Do not create/register profiles by writing undocumented Brave metadata. Creating
    profiles directly from BraveBackup would require separate research, supported behavior
    and tests before being offered.

## Review status

All fifteen items are reviewed, including the user's revised pause/rollback/resume flow.
The [phased Restore implementation plan](restore-completion-plan.md) is proposed for approval. The future diff/merge
tool remains
separate from replacement restore; see [its scope and KDiff3 investigation](diff-merge-future.md).
