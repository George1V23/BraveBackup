# Settings backup: next category checkpoint

Status: **all three groups approved with “keep all” and implemented**.
See [implemented behavior and validation](settings-backup.md). Bookmarks-only archive creation remains
unchanged. The next category in [the agreed sequence](backup-completion-plan.md) is
Settings. The existing decision kept “supported Brave profile preferences”; it did not
choose which preferences to include. The choices below record the approved scope.

## Proposed visible behavior

Keep the existing Settings checkbox and add a concise explanation of its supported
contents. Do not introduce new per-setting controls in this increment unless requested.
Selecting Settings backs up the following stored preferences for every selected profile:

| Group | Proposed contents | Source value types |
| --- | --- | --- |
| Bookmark bar | Stored visibility choices, including legacy show-on-all-tabs and the newer visibility-state value when present. | Boolean; integer. |
| Home | Show Home button, use New Tab as home, stored homepage address. | Boolean; boolean; string. |
| Startup | Stored startup mode and the ordered list of startup addresses. This is not a backup of open windows or tabs. | Integer; string array. |

The UI names these groups so “Settings” cannot be mistaken for all Brave settings.
Homepage/startup addresses are included as user data in the archive, including when
encryption is explicitly off, and never in logs or the operation report.

Preserve only values actually present in the profile Preferences snapshot. Record
which supported keys are absent, without inventing browser defaults. Preserve integer
values without inventing enum meanings. Invalid types, ambiguous duplicate keys,
unreadable/malformed input or size limits fail the entire selected backup. Unknown
preferences are outside this explicitly displayed scope and are not copied.
A valid Preferences object with no allowlisted values records zero stored supported
values, with that count visible in completion feedback; it is not reported as a full
settings backup. Missing Preferences is an error, not a synthetic empty result.

Settings may be selected alone or together with Bookmarks. A Settings-only operation
must not require a Bookmarks file. Other unfinished categories continue to block saving.
Read-only capture, overwrite confirmation, encryption defaults, compression, cancellation,
verification and private temporary-file handling remain the same as the bookmark workflow.

## Explicitly excluded from this increment

No entire Preferences/Secure Preferences/Local State file is archived. Do not include
account, sign-in, sync, wallet, rewards, authentication, extension databases/settings,
search-provider configuration, site permissions, Shields configuration, download paths,
OS key material or machine-specific state. These exclusions define the limited scope;
no complete-settings or portable-restore guarantee is made. Wider coverage requires
its own reviewed keys and fixtures.

No restore or live preference editing is added. A later Restore review must determine
how selected preferences are applied without overwriting unrelated settings, dealing
with protected preferences, browser policies and version differences. Saving startup
mode does not implement Sessions or recover the session that mode might refer to.

## Proposed archive extension

Preserve all existing archive-v1 reads and bookmarks-only writes. Archives containing
Settings use **archive manifest version 2**, with `settings-v1` as a required capability
and `bookmarks-v1` additionally when Bookmarks is included. The BBKENC1 encryption
envelope remains version 1; legacy standalone bookmark JSON remains schema 1.
Older readers must reject the new required version explicitly rather than skip Settings.

Each selected profile gets a category `{ id: "settings", version: 1, files: [...] }`
with exactly one `portable-settings` payload:

```text
installations/<installation-id>/profiles/<profile-id>/settings/allowlisted-v1.json
```

The payload is an application-owned UTF-8 JSON schema, not an invented native Brave
format. Proposed fields are `schemaVersion: 1`, `values` (a dictionary keyed by the
exact dotted preference names) and `absentKeys` (the other approved names). Each approved
key appears exactly once across values/absentKeys. Manifest payload length/SHA-256,
strict role/path checks and authenticated encryption cover it as usual.

Proposed capture limits: 32 MiB input Preferences snapshot and 1 MiB settings payload,
with bounded strings/list counts during extraction. Oversized data fails explicitly.
These are implementation resource limits, not claims about Brave's file-format limits.
No new package is proposed.

## Research evidence and limits

Primary sources inspected on 2026-11-16:

- [Chromium profile preference names](https://github.com/chromium/chromium/blob/main/chrome/common/pref_names.h):
  `browser.show_home_button`, `homepage_is_newtabpage`, `homepage`,
  `session.restore_on_startup`, `session.startup_urls`.
- [Chromium bookmark preference names](https://github.com/chromium/chromium/blob/main/components/bookmarks/common/bookmark_pref_names.h):
  `bookmark_bar.show_on_all_tabs`, `bookmark_bar.visibility_state`.
- [Chromium startup preference registration/handling](https://github.com/chromium/chromium/blob/main/chrome/browser/prefs/session_startup_pref.cc):
  mode is an integer, addresses are a list; this is startup configuration, not session data.
- [Chromium preference service](https://github.com/chromium/chromium/blob/main/components/prefs/pref_service.cc):
  preference names can be represented as nested dictionary paths; stored user values can
  be absent. Browser defaults/effective policy-controlled preferences are separate concerns.
- [Chromium JSON preference store](https://github.com/chromium/chromium/blob/main/components/prefs/json_pref_store.cc):
  JSON preference storage expects a dictionary root.
- [Brave profile preferences](https://github.com/brave/brave-core/blob/master/browser/brave_profile_prefs.cc):
  Brave adds/migrates preferences and registers multiple feature-specific stores. It is
  not valid to infer one universally portable “all settings” schema from Chromium names.

These are moving upstream source references, not verification of every installed Brave
version. They justify proposed key names/types and a research fixture; they do not prove
cross-version restore, defaults, or completeness. Implementation should pin the consulted
source revisions in its research note and add fixtures for missing/newer/invalid values.
No personal browser data was inspected to derive this proposal.

[Preferences.settings-proposed.json](fixtures/Preferences.settings-proposed.json) is a
**synthetic research example**, not a captured Brave profile. It demonstrates
nested input paths, explicit false values, the two visibility fields and unknown data
that must be excluded. Its example startup mode is taken from Chromium's published
constant mapping; the future reader should preserve integer values rather than depend
on that illustrative interpretation. The same input is now used by the Settings collector tests.

## Planned implementation and acceptance after scope approval

1. Add the Brave-specific bounded read-only settings collector and typed extraction
   in Backup, with research notes and fixtures. Reject ambiguous or malformed values;
   preserve false/empty values and absent-key distinctions.
2. Extend browser-independent archive contracts/verification to v2 required capabilities,
   mixed categories and declared settings JSON; retain v1 fixtures/read paths unchanged.
3. Connect Settings-only and Bookmarks+Settings requests, visible scope/count feedback,
   dynamic sanitized reports and existing encryption/progress/cancellation handling.
4. Test both protection modes, four compression choices, profile scopes, exact approved
   values, exclusion of synthetic unrelated data, missing/invalid/oversized input,
   cancellation, failed verification and unchanged browser inputs. Keep all current
   bookmark, HTML/JSON and archive-v1 tests. Document Windows runtime validation limits.
5. Return for user review before Extensions or another category begins.

Next checkpoint: review the implemented limited Settings scope before the next category.
Settings archives use manifest v2; bookmarks-only archives retain v1.
