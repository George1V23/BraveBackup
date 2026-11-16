# Tab Manager Plus backup: review checkpoint

Status: proposed; no application implementation authorized by this document.

The existing Extensions category can preserve raw extension files and local/sync stores.
It does not decode Tab Manager Plus saved windows. The dedicated category still blocks
backup as unsupported, and no dedicated project exists yet.

## Source findings

Inspected upstream revision `3937a32841441b7349673dddf0be895d67e9ef99`:

- [Saved-window type](https://github.com/stefanXO/Tab-Manager-Plus/blob/3937a32841441b7349673dddf0be895d67e9ef99/src/types/ISavedSession.ts)
  stores tabs, window information, name, color, timestamps, ID, custom-name/private flags
  and optional ordering/update fields.
- [Storage model](https://github.com/stefanXO/Tab-Manager-Plus/blob/3937a32841441b7349673dddf0be895d67e9ef99/src/popup/sessionStore.ts)
  describes a `sessions` object in extension-local storage, keyed by saved-window ID.
- [Official export format](https://github.com/stefanXO/Tab-Manager-Plus/blob/3937a32841441b7349673dddf0be895d67e9ef99/src/popup/sessionsFile.ts)
  defines `{format:"tab-manager-plus-export",version:1,kind:"sessions",sessions:[...]}`.
  Its importer also accepts other shapes; BraveBackup should initially accept only
  explicitly researched and tested formats, rather than copy that permissiveness.
- [Migration](https://github.com/stefanXO/Tab-Manager-Plus/blob/3937a32841441b7349673dddf0be895d67e9ef99/src/helpers/migrate.ts)
  consolidates older per-session records and moves legacy localStorage settings.
  One modern storage key is therefore insufficient to promise all historical versions.

These findings describe the inspected source, not a guarantee about every installed
store release. Official Chrome extension ID: `cnkdjjdmfiffagllbiiilooaoofcoeff`.
Do not identify compatible extensions by their display name alone.

## First decision: where saved windows come from

**Recommended: automatic profile capture.** Read a stable, read-only snapshot of the
identified extension's supported local storage while Brave is stopped. This fits the
application's detected-profile workflow. Before implementation, research Chromium's
storage encoding and select a supported LevelDB/Snappy reader strategy with fixtures.
Do not open the original database through a library that could repair or modify it.
Explain any proposed NuGet dependency and its purpose before adding it. Unsupported
legacy layouts must remain explicit failures until independently covered.

**Alternative: official JSON export first.** Add a file chooser per selected profile for
a file the user exported from Tab Manager Plus. Clearly label its manual provenance:
the app cannot prove that an arbitrary JSON file came from that profile. This avoids
the initial database reader but adds a manual step and is not automatic profile capture.
Do not silently substitute this alternative for the recommended workflow.

Either route needs user approval before implementation. A database-reader blocker is
reported for another decision, not replaced with raw-only success.

## Subsequent control and behavior choices

| Choice                      | Proposed behavior                                                                                                                                                                                                                                                                         |
|-----------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Dedicated category          | Keep TabManagerPlus separate from Sessions and Extensions, applying to all selected profiles. Identify supported installation/version explicitly.                                                                                                                                         |
| Saved-window contents       | Preserve saved names/colors, IDs, timestamps, ordering, ordered tab URLs/titles, pinned/active flags and window geometry/state where actually present and supported. Preserve absence instead of inventing values. Window colors are not a promise of native Chromium tab-group recovery. |
| Extension settings          | Defer decoded extension preferences and transient open-window bookkeeping; this increment captures saved windows. Existing Extensions raw capture remains separately available.                                                                                                           |
| Missing or unsupported data | Explain missing installation, unsupported version/layout, malformed or changing input and block the selected operation. A validated empty saved-window collection may succeed with zero counts; unavailable data must not become zero.                                                    |
| Privacy and feedback        | Encryption stays on by default. Warn that saved URLs/titles/private-window records are readable in unencrypted output. Reports show counts and provenance, not URLs, titles or private content. Never open tabs or modify extension data during backup.                                   |

## Implementation checkpoints after scope approval

1. Research and fixture checkpoint for the chosen input route; document supported
   versions, source consistency rules, limits and any dependency proposal.
2. Add a UI-independent project under `src/Extensions/TabManagerPlus`; implement bounded
   parsing and fixture tests, including empty, malformed and unsupported inputs.
3. Integrate portable saved-window payloads and validation into a documented archive
   capability/version, retaining readers for versions 1–4. Existing categories keep
   their established archive versions when TabManagerPlus is not selected.
4. Wire the existing category and truthful completion counts, with tests for mixed
   categories, failure/cancellation, destination preservation and unchanged profiles.
5. Review the working Backup increment before starting passwords or restore/import.

No restore, merge, extension installation, browser helper, or live-profile write is
included in this proposal. No package, application code or backup format changed here.
