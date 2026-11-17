# Password CSV view-model behavior, before layout integration

`PasswordCsvExportViewModel` prepares the standalone CSV UI behavior without adding or
changing an Export view. The user reserved visible-layout/control decisions for individual
review. The user chose a separate Password CSV section below bookmark export. The
[section/host integration](passwords-csv-section.md) is now implemented; existing bookmark
export is preserved and the class is connected to the selected export profile.

## Metadata context and readiness

`SetContext(installation, profile)` changes metadata only and never reads credentials.
It remembers destination text independently per profile, restores it when returning to that
profile, clears result state, and clears the provider selection when the installation
changes. Changing context during an operation is refused. The supported host provider list
reuses the approved provider descriptions and starts unselected.

Readiness requires one installation/profile, an explicitly selected supported provider,
the researched CSV version profile, a safe `.csv` destination in an existing folder outside
browser data, and an available confirmation callback. A shared metadata-only
`BravePasswordCsvExport.SupportsVersion` check keeps UI/backend compatibility rules aligned.
Unsupported versions show the limitation before any key/source access. Full destination
text is exposed without writing anything. Provider/context/destination changes do not
read passwords or start an export.

The dedicated `ExportCsvCommand` invocation is the explicit CSV format choice. It does not
stand in for the required fresh destination-specific warning. The host must supply that
warning callback; a missing callback leaves the action unavailable. No extra unreviewed
checkbox or control was added by this behavior checkpoint.

## Frozen operation, consent and cancellation

Starting the explicit action freezes installation, profile, provider, destination and host
callbacks, then invokes the completed capture/group/project/confirmed-save API. Secret owners
and captured sources stay in the backend, not observable properties. The view-model provides
busy state, a dedicated cancellation command and generic stage text without fabricated byte
progress. A future view must disable editable controls while busy; programmatic changes to
source/destination properties cannot change the frozen request.

Each operation obtains fresh warning approval. Decline/cancel preserves existing files.
Disposal cancels in-flight work, and operation cleanup owns cancellation-source disposal
until work has actually stopped. Successful completion exposes the actual frozen output
path separately from a count-only report. Unexpected host/IO/crypto error text is not copied
into status; only sanitized backend operation messages are used. Sites, saved usernames,
passwords and notes never enter status/report. No credential preview is provided.

## Validation and next review

Eight synthetic tests cover unavailable/unselected source/dialog guards without key access,
repeated fresh approval and safe results, per-profile destination restoration, installation
source reset, frozen destination/provider values, blocked in-flight context changes, cancel/
dispose during confirmation, existing-file preservation, unexpected-message redaction and
version rejection. Test credentials/keys/files are synthetic and disposable; no real keyring
or profile was accessed. The full build compiles the new model without changing UI layout.

The selected layout now connects the profile, provider selector, destination picker,
CSV action/cancel and warning dialog through the host. Broader native
compatibility, CSV-in-archive contracts/consent and real window interaction checks remain
pending. No restore behavior or archive format changes were introduced.
