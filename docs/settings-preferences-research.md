# Brave stored-preference research

Inspected 2026-11-16. This note supports the limited [Settings backup](settings-backup.md).
It does not define an undocumented Chromium restore mechanism or infer effective values.

## Pinned primary sources

Chromium revision `0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0`:

- [chrome/common/pref_names.h](https://github.com/chromium/chromium/blob/0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0/chrome/common/pref_names.h)
  defines Home-button, homepage/New Tab, startup-mode and startup-address preference names.
- [components/bookmarks/common/bookmark_pref_names.h](https://github.com/chromium/chromium/blob/0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0/components/bookmarks/common/bookmark_pref_names.h)
  defines legacy Boolean bookmark-bar visibility and integer visibility-state paths.
- [chrome/browser/prefs/session_startup_pref.cc](https://github.com/chromium/chromium/blob/0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0/chrome/browser/prefs/session_startup_pref.cc)
  registers startup mode as an integer and startup addresses as a list. Capturing those
  choices does not capture session windows/tabs. Unknown integers are preserved, not decoded.
- [components/prefs/pref_service.cc](https://github.com/chromium/chromium/blob/0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0/components/prefs/pref_service.cc)
  distinguishes user-stored values from defaults and constructs dictionaries using dotted
  paths. Backup therefore records stored absence without substituting guessed defaults.
- [components/prefs/json_pref_store.cc](https://github.com/chromium/chromium/blob/0fc15473022d1b166b6a5eb937ef2a3dffc3c2e0/components/prefs/json_pref_store.cc)
  reads dictionary JSON and stores preferences with nested dotted paths.

Brave revision `b7cef2395be549efa53406b6a5b432f33df7fab5`:

- [browser/brave_profile_prefs.cc](https://github.com/brave/brave-core/blob/b7cef2395be549efa53406b6a5b432f33df7fab5/browser/brave_profile_prefs.cc)
  registers/migrates Brave-specific feature preferences. The narrow seven-key scope does
  not establish that arbitrary Brave settings are portable or can be safely replaced.

Revisions were resolved through the official GitHub API and the pinned raw source files
were fetched directly. The symbolic main/master sources used during the initial proposal
were not assumed immutable. Native file contents were not copied from an installed browser.

## Decisions and unresolved behavior

The collector follows nested paths, accepts the defined primitive types and records
missing values explicitly. Duplicate JSON names, invalid parent/value shapes and resource
limit violations fail capture. Unrelated data is excluded even when adjacent to included
keys. Retaining both bookmark visibility fields preserves what is stored; the collector
does not decide which takes precedence during a future restore.

Actual effective settings may be controlled by defaults, policy, another store or Brave
version differences. Only explicit values in Preferences are backed up in this slice.
Secure Preferences, Local State, native integrity metadata and version-specific restore
handling remain outside scope. No statement here proves restoration on another system.

Tests use a synthetic nested Preferences fixture with false/empty values, ordered example
addresses and unrelated exclusion markers. Generated variants cover missing/malformed,
duplicate and oversized values. The application-owned allowlisted payload and archive-v2
fixture are specified and tested independently of the native file's entire schema.
