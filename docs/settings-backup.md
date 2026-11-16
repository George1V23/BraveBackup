# Supported Settings backup and archive v2

The user kept all three proposed Settings groups. The Backup tab now supports Settings
alone or with Bookmarks. It retains the existing single Settings checkbox and describes
its limited contents. It does not implement the separate Settings navigation page or
any restore, import or live-edit operation.

## Included data

Only the following stored values from the selected profile's `Preferences` are included:

| Group | Exact preference path | Accepted value |
| --- | --- | --- |
| Bookmark bar | `bookmark_bar.show_on_all_tabs` | Boolean |
| Bookmark bar | `bookmark_bar.visibility_state` | Signed 32-bit integer |
| Home | `browser.show_home_button` | Boolean |
| Home | `homepage_is_newtabpage` | Boolean |
| Home | `homepage` | String |
| Startup | `session.restore_on_startup` | Signed 32-bit integer |
| Startup | `session.startup_urls` | Ordered array of strings |

Preserve false, empty strings, empty arrays, address order and integer values. Missing
keys are recorded as absent without inventing defaults or effective policy values.
No enum mapping is inferred from an unknown integer. Startup mode is configuration;
recording it does not back up the open windows/tabs to which it may refer.

No complete Preferences, Secure Preferences or Local State file is archived. Unrelated
account, sync, wallet, rewards, extension, machine-specific and other preference values
are excluded. The UI describes the approved scope before creation and explains that
homepage/startup addresses are readable when archive encryption is off. The addresses
are never included in logs or operation reports.

A valid empty Preferences object records zero stored supported values. Completion and
report show stored/absent counts in total and stored counts per profile in selection
order, without profile names in the report. A missing file or malformed/ambiguous value
fails the entire requested backup; no profile/category is silently dropped. Settings-only
backups do not read or require Bookmarks.

## Capture and validation

`BraveSettingsBackup.Capture` performs at most three bounded read attempts with
length/mtime checks and cancellation. The source limit is 32 MiB. `Extract` requires a
JSON object, rejects duplicate JSON names, follows nested exact-case paths and copies
only approved values. A present supported path with a non-object parent, null value or
wrong type is an error. Unknown paths are not interpreted. A UTF-8 BOM is accepted.

Individual strings are limited to 32,768 UTF-16 code units; address lists to 1,024 items.
The serialized payload limit is 1 MiB. These are application resource limits, not native
Brave format limits. Capture never creates or modifies profile files. Snapshot buffers
are cleared on completion/failure. Managed strings/parser objects are not promised secure
erasure. As before, reading while Brave runs does not create a browser-wide transaction.

Core's `PortableSettings.Parse` validates the browser-independent payload shape,
primitive types/limits and disjoint present/absent keys. Backup's
`BraveSettingsBackup.ValidatePayload` additionally enforces the exact approved seven-key
union and corresponding types. `ValidateArchive` authenticates/validates archive contents
through Core, then applies the browser-specific Settings policy before publication.
An attacker cannot make an unknown Settings key pass this check by recomputing its SHA-256.
Core deliberately does not know native Brave preference names.

## Archive-v2 contract

Bookmark-only creation still writes [archive v1](archive-format-v1.md), and the original
v1 golden fixture continues to validate. Existing standalone JSON schema 1 and HTML
export remain unchanged.

Archives containing Settings use `format: "BraveBackupArchive"`, `formatVersion: 2`.
The manifest structure and UUID identity rules remain the same as v1. Required
capabilities are exactly one of the following arrays, in this canonical order:

```json
["settings-v1"]
["bookmarks-v1", "settings-v1"]
```

Every profile contains each requested category exactly once. Bookmarks keeps its
unchanged two payloads/roles. Settings category version 1 has exactly one file with
role `portable-settings`, at:

```text
installations/<installation-id>/profiles/<profile-id>/settings/allowlisted-v1.json
```

This UTF-8 application-owned payload uses camelCase JSON fields:

```json
{
  "schemaVersion": 1,
  "values": {
    "browser.show_home_button": false,
    "homepage": ""
  },
  "absentKeys": [
    "bookmark_bar.show_on_all_tabs",
    "bookmark_bar.visibility_state",
    "homepage_is_newtabpage",
    "session.restore_on_startup",
    "session.startup_urls"
  ]
}
```

This schema is not a native Preferences file. Every approved key appears exactly once
in values or absentKeys. Payload length/SHA-256, canonical role/path and the 1 MiB limit
are verified. Duplicate/missing/unknown categories, wrong versions/capabilities, JSON,
roles, paths, keys/types and lengths/hashes are rejected. Older v1-only readers reject
manifest v2 clearly; the current reader retains v1 support. No archive version is
silently downgraded or category ignored.

BBKENC1 encryption remains envelope version 1, with the same key derivation, authenticated
chunks and terminal record. Both protection modes and all compression choices work for
Settings-only/mixed archives. Private temporary files, overwrite confirmation, cancellation,
verification before publication and source-directory protection remain in force.

`BraveArchiveBackup.CreateAsync` freezes the categories and captures only those selected.
`BackupResult.SettingsSummary` supplies safe counts to completion feedback. Reports list
the actual requested/completed categories; failure reports also use the frozen category
list. The UI handles validation errors without exposing raw exception content. No NuGet
package, CLI command, password export or restore behavior was added.

## Research and fixtures

See [the pinned research note](settings-preferences-research.md) for official source
revisions and limitations. The source fixture `tests/Core.Tests/Fixtures/Preferences.settings.json`
is synthetic, not copied from a personal profile. It contains both approved values and
unrelated fields to verify exclusion. The prior [proposal fixture](fixtures/Preferences.settings-proposed.json)
is the same illustrative input. Additional malformed/absent/oversized variants are created
by tests. The source-owned Settings payload format is specified above.

`Archive.v2.settings.bravebackup` is an independent frozen Python-generated ZIP fixture;
its generator is `tests/Core.Tests/Fixtures/generate-settings-v2-vector.py`. Existing
archive-v1, schema-1 JSON and encrypted envelope fixtures were not changed.

## Validation and next checkpoint

All 301 solution test cases pass on Linux: 234 Core, 48 Brave, 13 Linux, 6 Windows-labelled
cases. These include Settings-only/mixed scopes across both protection modes and all
four compression levels; exact values/absence/exclusion; malformed/oversized inputs;
strict v2 metadata and rehashed invalid payloads; cancellation/cleanup; unchanged sources;
UI validation/count feedback; and existing v1/HTML/JSON regressions.

A Linux desktop fixture toggled the actual Settings checkbox, invoked Create Backup,
verified an encrypted mixed v2 archive, checked completion/report counts and confirmed
that both Bookmarks and Preferences sources were unchanged. It emitted no binding warnings.
[Review screenshot](screenshots/settings-completion.png) uses synthetic data.

Native Windows execution and independent cryptographic review remain release checks.
Restoring these saved preferences still requires a reviewed, version-aware restore
workflow; no cross-version portability is promised. The next user checkpoint is review
of this Settings increment before Extensions or another category starts.
