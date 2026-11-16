# Brave-compatible bookmark HTML export

## Research sources

Brave's [official import/export instructions](https://support.brave.app/hc/en-us/articles/360019782291-How-do-I-import-or-export-browsing-data)
identify the Bookmarks Manager export as an HTML file. Serialization was checked against
Chromium's published [bookmark HTML writer](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/bookmarks/bookmark_html_writer.cc),
including its header, nesting, root handling, attribute order and escaping. The inspected
writer had blob ID `92bf8998d58fdcc51f6459736845ad6d0236ba28` (reviewed 2026-11-16).
Date conversion was checked against [Time::ToTimeT](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/base/time/time.h);
title escaping against [EscapeForHTML](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/base/strings/escape.cc).

These are published format rules, not a guessed Brave binary format. Chromium is licensed
under BSD-3-Clause; the existing [Chromium license](licenses/CHROMIUM-LICENSE.txt) provides
its attribution and terms. This implementation follows the serialization conventions;
it does not copy browser profile databases or launch a browser to export.

## Output contract

`BraveBookmarksHtmlExporter` in Backup produces Netscape bookmark HTML:

- `NETSCAPE-Bookmark-file-1` doctype, UTF-8 metadata and Bookmarks title/header;
- UTF-8 without BOM, CRLF line endings and four-space nesting indentation;
- the bookmark-bar folder has `PERSONAL_TOOLBAR_FOLDER="true"`;
- bookmark-bar descendants are nested under that folder; Other/Mobile descendants are
  emitted directly afterward, matching the browser writer rather than adding wrappers;
- each URL is an `A` entry with `HREF` and `ADD_DATE`; nested folders use `H3`, `ADD_DATE`,
  `LAST_MODIFIED` and nested `DL` sections;
- titles escape markup while keeping Unicode text. URL attributes use the upstream
  writer's double-quote escaping and retain the stored canonical URL string;
- Chromium's microseconds since 1601 are converted to floored UTC Unix seconds. Null
  timestamp `0` remains zero; missing/malformed optional timestamps also use zero, never
  an invented current time. The 64-bit infinity sentinels retain their sentinel values.

Additional unrecognized roots are retained as ordinary folders to avoid silent data loss.
Duplicate root keys cause a clear error. Empty/partial files supported by the existing
parser can still produce an empty/partial valid bookmark HTML document.

`BraveBookmarksParser` now retains optional root creation/modification timestamps through
additional init-only properties on `BraveBookmarkRoot`; its existing constructor signature
is preserved. Malformed optional root timestamp types are ignored. The schema-1 JSON
export still omits these fields, and its frozen compatibility fixture remains unchanged.

## Scope and fidelity

This is the same importable HTML *format* used by Brave's manual export, not a promise of
byte-identical output for every live browser state. The source is the selected profile's
`Bookmarks` file. Separate account stores are not merged. Optional `ICON` favicon images
are not embedded: those bytes live outside the Bookmarks document, and this increment
reads no favicon database. The UI explicitly states this limit. Link titles, URLs, folders,
ordering and available timestamps are retained using the browser's root layout.

## UI behavior

HTML is first and selected by default in Export panel 2. JSON remains a separate choice
using the unchanged BraveBackup schema version 1. The format choice updates the default
extension, editable filename, action label, native picker filter and MIME type. Switching
formats preserves the chosen directory and basename; profile switching preserves custom
filenames. A conflicting known extension (.json in HTML mode or .html/.htm in JSON mode)
shows an explanation and prevents accidental wrong-format output.

Full destination display, stable Documents/home export folder and explicit success/error
feedback remain as described in [export-destination-fix.md](export-destination-fix.md).
Both formats use the same destination protections and temporary-file replacement policy.
No browser profile is modified. No NuGet package was added. CLI JSON behavior is unchanged.

## Fixtures and verification

`tests/Brave.Tests/Fixtures/Bookmarks.html-export.json` and its expected HTML are synthetic,
hand-authored fixtures based on the published writer, not a captured personal browser
export. They exercise reordered roots, toolbar marking, flattening, nested folders,
Unicode, title escaping, dates and null timestamps. Tests additionally cover quoted URLs,
pre-epoch fractional dates, malformed optional metadata, extra/duplicate roots, UTF-8,
CRLF, cancellation, cleanup and rejection of profile destinations.

UI/view-model tests verify primary HTML, preserved JSON schema 1, extension updates,
custom path preservation, mismatched-extension feedback and notifications. A Linux
fixture-window check exercised the real format dropdown and real Export button in both
modes. An independent Python HTML parser verified the emitted URLs, toolbar marker and
Unicode title. This did not import data into a live browser profile.
