# DRAFT: Restore execution controls (for your review)

Status: **proposal, nothing implemented.** Items 1–9 and 15 of the [accepted controls](restore-ui-review.md) already
exist (file/unlock/preview, source and category selection, destination mapping, safety folder and
passwords). This draft covers the remaining accepted behavior, items 10–14, and what changes in the existing
controls once the bookmarks writer ([restore-bookmarks.md](restore-bookmarks.md)) exists. It adds **no
control that is not already described in those decisions**, except those listed under "New decisions needed".
Wording and layout are proposals; the engine calls are real.

## A. Changes to existing controls

| Control | Today | Proposed |
|---|---|---|
| Category checkbox `bookmarks (v1)` | disabled, "writer not enabled" | Enabled when the profile has a `native-source` snapshot ≤ 64 MiB of Chromium version 1, the host is Linux, and the destination passes the checks below. Otherwise disabled with the exact reason. |
| Every other category | disabled | unchanged (still "not enabled yet") |
| Destination status line | read-only checks | adds per-destination "Bookmarks: will replace existing file" / "will be created from this profile's Preferences" / **blocked:** "open this profile in Brave once, then refresh" |
| Windows host | n/a | bookmarks disabled with "Windows restore is not validated yet" (see decision 4) |

## B. New elements, in page order

```
┌ Restore sources and destinations ─────────────────────────────┐  (existing card)
│ ...selection, mapping, Recheck, plan summary...               │
└───────────────────────────────────────────────────────────────┘
┌ Safety backup ────────────────────────────────────────────────┐  (existing card)
│ Folder [ /home/me/safety        ] [Browse]                    │
│ Password [••••••]  Confirm [••••••]   (cannot be recovered)   │
└───────────────────────────────────────────────────────────────┘
┌ Review ───────────────────────────────────────────────────────┐  NEW, item 11
│ [Review restore]   ← enabled only when: ≥1 supported category │
│                      selected, mappings valid, all destination │
│                      installations Closed, safety folder valid,│
│                      passwords non-empty and equal             │
└───────────────────────────────────────────────────────────────┘
```

### B1. Review restore → confirmation panel (item 11)

Pressing **Review restore** freezes the selection (no write) by creating the session
(`BraveBookmarksRestoreSession.Create`, which validates and writes nothing) and shows:

```
┌ Confirm restore ──────────────────────────────────────────────┐
│ Source backup:   /home/me/backups/work.bravebackup            │
│                                                               │
│ Brave Stable 1.80 (installation)                              │
│   "Work" (backup)  ──►  Default  /home/me/.config/.../Default │
│       Bookmarks: REPLACES the destination's current Bookmarks │
│ Not changed: all other categories and profiles.               │
│                                                               │
│ Safety backup (encrypted):                                    │
│   /home/me/safety/<id>.safety.encrypted                       │
│ Brave must stay closed. Replacement is not merged.            │
│                                                               │
│                      [Cancel]   [Confirm restore]             │
└───────────────────────────────────────────────────────────────┘
```

Cancel disposes the session (nothing was written). Confirm calls `Prepare()` then `Run()`. Changing any
selection while the panel is open closes it and requires a new review.

### B2. Progress (item 12)

```
┌ Restoring ────────────────────────────────────────────────────┐
│ [████████░░░░░░░░] 1 of 2 files          (indeterminate until │
│                                           Prepare finishes)   │
│ Brave Stable 1.80                                             │
│   Work → Default                                              │
│     ✓ Bookmarks                                               │
│   Personal → Profile 1                                        │
│     … Bookmarks                                               │
│                                  [Cancel restore]             │
└───────────────────────────────────────────────────────────────┘
```

Driven by `RestoreTransaction.Progress` (completed/total files, category completed). All inputs above are
disabled while running. **Cancel restore** calls `RequestPause()`.

### B3. Paused → rollback question (item 12)

After the engine reaches `Paused`:

```
┌ Restoration paused ───────────────────────────────────────────┐
│ Changes already made by this restore: Work → Default / Bookmarks │
│ Roll back the changes made so far?                            │
│                       [Roll back]   [Not now]                 │
└───────────────────────────────────────────────────────────────┘
```

**Not now** leaves the operation paused and shows **Resume restoration**. **Resume** calls `Run()`, which
revalidates source, destinations and stopped-browser state; on failure the panel stays paused and shows the
sanitized reason. **Roll back** calls `Rollback(true)`. Nothing resumes or rolls back automatically.

### B4. Rollback progress (item 13) and report (item 14)

Rollback reuses the progress layout titled "Rolling back" with `✓ Recovered` per category. The final report shows
one of **Restored**, **Rolled back**, **Failed / recovery incomplete**, grouped by installation, profile and
category, the safety and journal paths, **Open safety-backup folder**, and **Save detailed report** (stages,
counts, sanitized codes; no credentials or secrets). `RecoveryIncomplete` is never shown as success, and the
remaining safety paths are listed.

## C. State machine

```
Idle ─Review─► Reviewing ─Cancel─► Idle
                 │Confirm
                 ▼
             Preparing ─error─► Failed(report)        (nothing in the browser changed)
                 ▼
             Running ──Cancel restore──► Pausing ─► Paused ─Resume─► Running
                 │                                      │Roll back
                 ▼ done                                 ▼
             Completed(report)                  RollingBack ─► RolledBack | RecoveryIncomplete (report)
```

Engine mapping: Preparing=`Prepare`, Running=`Run`, Pausing/Paused=`RequestPause`/`Paused`, RollingBack=`Rollback(true)`;
`Failed` from `Run` offers the same rollback choice as Paused.

Leaving the page or closing the app while Preparing/Running/Paused/RollingBack: see decision 3.
Passwords (safety and source) are cleared on Completed, RolledBack, Failed, and page exit, per items 2 and 9.

## D. New decisions needed from you

1. **Rollback after completion.** The engine can roll back from `Completed`. Accepted items only offer rollback after
   a pause or failure. Add a "Roll back this restore" button to the completed report, or keep it hidden?
   *Recommendation: add it; it uses the same confirmation and the safety backup is already there.*
2. **Absent-destination wording.** Is "will be created from this profile's Preferences permissions" too technical?
   Proposed user text: "Bookmarks file will be created."
3. **Closing during an operation.** *Recommendation:* block closing the window while Preparing/Running/RollingBack
   with a short message, allow it while Paused (artifacts are retained), and show the safety path on next open.
   No restart-resume exists, so a paused operation cannot continue after the app closes; say that plainly.
4. **Windows.** Keep the bookmarks category disabled on Windows until the native tests pass, or enable it with a
   warning? *Recommendation: keep disabled.*
5. **Progress granularity.** Per file (1 of 2) is what the engine reports. Acceptable, or show only the
   per-category checklist and an indeterminate bar?
6. **Per-destination Recheck.** Item 10 asks for Closed/Running/Unable per destination installation; the existing
   Recheck button covers all targets with a combined message. Show a status line per installation?

## E. Implementation plan once approved

1. `RestoreExecutionViewModel` owns the session and the state machine above, with injected observer/dispatcher so
   it is testable without Brave; engine calls run off the UI thread, results marshal back.
2. Views for B1–B4 plus gating in `RestoreCategoryChoice`/`RestorePlanningViewModel`.
3. View-model tests with synthetic archives (confirm, cancel at each stage, pause/resume, rollback, failure, password
   clearing) and a native Avalonia smoke run with screenshots, as done for the safety settings.
4. Documentation updates and a full regression run.
