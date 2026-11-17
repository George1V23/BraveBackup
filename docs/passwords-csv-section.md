# Approved Password CSV section in Export

The user chose a separate **Password CSV** section below bookmark export. The Export tab
now scrolls both sections together; the existing bookmark view/profile list remains in
place. Password CSV uses the profile selected in that list, including real profiles without
bookmarks. It does not introduce another profile picker or affect bookmark output format.

The new section provides the approved initially unselected provider choice/descriptions,
a per-profile CSV destination textbox and native Browse callback, full destination path,
readiness/compatibility explanation, dedicated **Export password CSV**, cancellation,
indeterminate busy feedback, safe count report and actual completed destination. No saved
username/password/site/note preview is shown. Its explanatory text states plaintext risk
before action; the actual save still requires separate fresh confirmation.

## Host integration and consent

The shell connects installation/profile metadata from the existing exporter to the tested
CSV view-model. Repeated notifications for an unchanged context preserve completion
feedback; switching contexts restores each profile's destination and clears provider choice
when installation identity changes. Selecting, browsing or changing context does not access
credentials. The current researched Brave-version/provider/URL/note compatibility gates
remain visible and unchanged; unsupported profiles cannot silently export partial CSV.

The window's native save picker chooses a local `.csv` path without application file writes.
Cancel keeps the existing destination. The fresh owned-window warning shows the exact
output path, readability by anyone with access, temporary plaintext staging and existing-file
replacement details. Save requires an explicit **Save plaintext CSV** decision; closing or
cancelling declines. Worker callbacks dispatch to Avalonia's UI thread; operation cancellation
closes the warning without approval. No OS password is collected by the application.

The backend still captures/validates/group-projects on a worker, disposes provider keys,
verifies password and affiliation sources through publication, privately stages, verifies
and atomically saves only after confirmation. Existing destination/failure cleanup rules
apply. The full output path is separate from the value-free count report.

## Interaction interlocks and lifetime

CSV export freezes navigation/rediscovery and bookmark export; its own Cancel stays usable.
Backup or bookmark activity disables password CSV. The view-model host-availability guard
also prevents starting its command programmatically during conflicting work. Profile/source/
destination fields are disabled during CSV work, and frozen requests remain independent of
later property changes. No synthetic progress percentage is fabricated.

Shell disposal cancels CSV and releases its event handlers; CSV disposal clears host dialog,
picker, observer and owned-key adapter references after cancelling. The operation keeps its
frozen callbacks/context until cleanup completes and owns cancellation-source disposal.
Backup's raw consent/key callbacks are also released on shell disposal. Existing archive
and bookmark workflows remain covered by regressions.

## Validation and limitations

Five added tests cover native-picker callback success/cancellation without writes or key
access, profile-bound asynchronous picker results, shell context/result preservation and per-installation
destination/source switching,
CSV busy navigation/refresh/conflicting-action interlocks with cancellation, and the
view-model host-availability guard. Existing CSV tests cover fresh/repeated warnings,
exact synthetic output, unsupported versions, frozen scopes and cancellation cleanup.
Compiled Avalonia bindings validate the new view and context boundaries in the build.

No real credentials/keyring was accessed. The later [synthetic Linux UI smoke test](passwords-ui-smoke.md)
verified native layout, selector bindings and CSV/raw-preservation dialog interaction.
The native file picker, real OS unlock prompts and Windows execution remain untested. CSV remains
limited to the researched version/provider/web/note compatibility profile. Broader support,
CSV entries inside backups and their encrypted/unencrypted consent contracts remain pending.
No restore behavior or archive format changes were introduced.
