# Future offline diff/merge tool

Requested after the Restore control review. This remains later work within **Profiles → Merge/Sync**, separate from the
approved replacement-restore behavior.

## Requested scope

Compare detected Brave application types/installations, profiles and their Sessions,
Bookmarks, Passwords, Extensions and Settings. Allow the user to choose individual data
and explicitly select the installed Brave application/profile into which it will merge.
Retain the project's offline operation requirement. Existing TabManagerPlus data remains
part of the project scope and needs an explicit merge design when this phase is reviewed.
Controls and conflict policies require the user's later individual review.

## KDiff3 candidate: preliminary findings

The user suggested evaluating source/library integration from
[KDE/kdiff3](https://github.com/KDE/kdiff3). The upstream README describes two-/three-way
file and directory comparison/merging, and documents command-line invocation. It states
GPL version 2 or later. The current build scripts inspected on 2026-11-17 build a C++17
executable using Qt 6 and KDE Frameworks 6, rather than providing a ready-to-reference
.NET library:

- [README](https://github.com/KDE/kdiff3/blob/master/README)
- [Top-level build](https://github.com/KDE/kdiff3/blob/master/CMakeLists.txt)
- [Executable target](https://github.com/KDE/kdiff3/blob/master/src/CMakeLists.txt)

These are moving upstream references, not a pinned integration dependency. No source
has been copied, package added, executable installed or integration choice finalized.
Before reuse, pin a revision, inspect the exact files' licenses and dependencies, and
assess distribution/license compatibility. Compare an optional external-process adapter
with extracting or porting selected code; do not assume source reuse will be simpler.

KDiff3's text/file comparison may help visualization or comparison algorithms. It does
not itself establish safe Brave-specific semantic merging of SQLite credentials, session
records, extension stores or settings. BraveBackup still needs typed comparison models,
identity matching, explicit source/target selection, conflict policies and category-aware
writers with fixtures/tests. Do not merge live browser databases as generic text/files.

Password comparison/access must remain on demand; do not send plaintext credentials
to external tools or temporary files without a separately designed, explicitly reviewed
flow consistent with the project's password rules. Preview and comparison must not
modify profiles. Any applied merge must be an explicit write operation with closed-browser
checks, verified safety backups and tested recovery, following the same project safeguards
as restore. No implementation is authorized merely by recording this future scope.
