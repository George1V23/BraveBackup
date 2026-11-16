# Existing bookmark backup format (schema 1)

This documents the existing JSON export; the project split does not change it.
It is not the planned `.bravebackup` archive format.

The UTF-8 JSON object has PascalCase properties:

- `SchemaVersion`: integer `1`.
- `Bookmarks`: an ordered array of root folders.
- Each bookmark has `Type` (numeric `0` for folder, `1` for URL), `Name` (string),
  `Url` (string for URLs, otherwise null) and `Children` (ordered array, empty for URLs).

Export uses indented System.Text.Json defaults, including default character escaping.
It preserves root/child order, names, URL strings and hierarchy; it intentionally omits
Chromium identifiers, GUIDs and timestamps. No password, setting, extension or session
data is stored. The schema version stays 1. There is no restore/import workflow yet.

`tests/Core.Tests/Fixtures/Backup.v1.json` is the frozen canonical representation of
`Bookmarks.sample.json`. Regression tests verify the old data can still deserialize and
that new exports have identical JSON values and property names. Future formats must
preserve the ability to read this version and document any version changes.
