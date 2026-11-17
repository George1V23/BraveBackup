# Restore phase 2: source selection and destination mapping

The approved source/profile choices, explicit destination mapping and read-only preflight
are connected below the validated archive inventory. Restoration remains unavailable.
No safety archive, journal, category writer or browser-data write is introduced here.

## Selection and capability

`RestorePlanningViewModel` loads archive-local source IDs, grouped by archived installation.
Profiles start unselected with Select all/Clear all shortcuts. Each profile owns independent
category choices and target mapping; matching display names never imply identity.

Only categories present in the validated archive appear. All category writers remain
unfinished at this checkpoint, so every category is visibly unsupported/disabled with a
reason, including the category-selection shortcuts when there are no supported categories.
Programmatic selection attempts cannot enable unsupported categories. This does not claim
that successful archive validation establishes a usable destination writer.

`RestoreProfileChoice` exposes two initially empty selectors: detected installation and an
existing profile belonging to it. The full normalized profile-directory path is displayed.
Changing installations clears the profile selection. Choices are separate from sidebar
activation and Backup selections. Rediscovery replaces target metadata and clears mappings
rather than silently matching old choices. New destination profiles are created in Brave.

Duplicate normalized destinations are rejected, including lexical `..` aliases and
host-appropriate case comparisons. Foreign/stale inventory objects cannot form a valid
mapping. The read-only plan lists selected sources, exact targets and check results;
its availability never presents an executable restore action.

## Read-only target and browser checks

Recheck freezes the current mapping and runs on a worker. `BraveRestoreTargetGuard.Validate`
requires an immediate-child profile of its installation's user-data root, rejects filesystem
roots as user-data destinations and missing/linked/unsafe ancestor directories, and rejects
source archives inside destination browser data. Directory enumeration checks basic read
access without opening credential/database contents or creating a write-permission probe.
It does not prove write access or destination format compatibility.

Platform's existing process probe conservatively covers visible Brave processes across
installations. Results distinguish Closed (no observed Brave process), Running and Unable
to verify. A running installation can block all destinations; no process is terminated.
Injected observers support synthetic tests without inspecting real credential stores.
These are point-in-time observations, not guarantees against a concurrent browser launch.

Checks are invalidated when source selection/mapping changes, discovery replaces targets,
the source changes, or Restore is left/disposed. Late worker results are discarded; each
worker owns/disposes its cancellation source. Original phase-1 password/preview clearing
rules continue to apply, and clearing the preview also clears this plan.

## Limits before execution

Preflight rejects common normalized/link aliases but does not establish physical directory
identity across bind mounts or unusual filesystem case/alias rules. Later execution must
establish supported filesystem identity, revalidate sources/targets/processes, prevent path
races and create verified safety backups before any modification. No readiness from this
phase can bypass those checks. All category compatibility remains blocked until the tested
writers and safety engine are delivered in later phases.

## Validation

Nine new test cases cover initial/independent choices, unsupported categories, explicit
mapping and clearing, duplicate/normalized paths, foreign target objects, Closed/Running/
Unknown results, inaccessible/missing/linked paths, source overlap, observer failures,
source preservation and stale rechecks across rediscovery/reset.

The full solution run passed 1,035 tests with six environment-dependent tests skipped.
The solution build passed with zero warnings/errors. The Linux native runner passed all
43 checks, including actual source-checkbox and destination-dropdown bindings, full-path
mapping, disabled unsupported categories and controlled Recheck feedback. Synthetic source
bytes were unchanged. See [mapping screenshot](screenshots/restore-phase2-mapping.png).
No new package or archive version was added. Windows desktop validation remains untested.

Next is phase 3: the encrypted safety snapshot, modification journal and tested
pause/resume/rollback engine, initially against synthetic directories only.
