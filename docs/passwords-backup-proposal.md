# Passwords: scope review

Status: all seven scope items reviewed and accepted, with the revisions recorded below.
The encrypted-source [snapshot component](passwords-snapshot.md) is implemented and tested;
[provider-aware decryption](passwords-decoding.md), the [Windows key adapter](passwords-windows-key.md)
and the [Linux Secret Service adapter](passwords-linux-key.md) are implemented as isolated
components, now connected by [explicit capture orchestration](passwords-capture.md).
The [portable payload codec](passwords-payload.md) is implemented independently of archive saving.
The [CSV formatting component](passwords-csv.md) is tested for already-projected rows.
[Confirmed standalone CSV saving](passwords-csv-save.md) is implemented for prepared rows.
[Explicit raw/native fallback capture](passwords-raw-fallback.md) now includes the host
raw-or-cancel choice. Real OS/keyring validation, browser CSV projection, encrypted-container
consent, raw/CSV archive writing and UI integration remain pending.
[Protected portable archive writing](passwords-protected-backup.md) is implemented through
explicit advanced options without enabling discovery or UI credential access.
[Archive-v6 protected portable payload validation](archive-format-v6.md) is implemented. The user's
protected default and explicitly confirmed optional plaintext CSV remain requirements.
The subsequent [research/fixture checkpoint](passwords-research.md) records pinned sources,
six passing fixture checks and unresolved native/live-capture/automation limits.

## Current implementation

The Passwords category exists but correctly blocks archive creation. Encryption defaults
on. The current warning appears when Passwords is selected and archive encryption is off;
there is no independent Protect passwords control or CSV export operation yet.

`BraveProfileDetailsReader.GetPasswordEntries` checks the presence of Login Data and
optionally reads a companion test/mock `Login Data.json`. For a real database it returns
a file-presence placeholder, not parsed credential rows or an actual credential count.
The CLI's existing metadata display is likewise not password extraction. Neither is an
acceptable source for a completed portable password backup.

A bounded SQLite snapshot reader and isolated Windows DPAPI adapter now exist, but
credential unlocking is not connected to user operations. Do not reinterpret copied
encrypted browser files as portable credentials.

## What was actually accessed

-

Brave's [password-manager help](https://support.brave.app/hc/en-us/articles/360018185951-How-do-I-use-the-built-in-password-manager)
documents manual CSV export.

-

Brave's [sensitive-data storage documentation](https://support.brave.app/hc/en-us/articles/29808985123085-Sensitive-data-storage)
describes OS-bound protection, Windows DPAPI and Linux password-store backends.
That page was updated in 2024; it is background, not proof that every current Brave
build still uses only those historical schemes.

- Pinned Chromium revision `c0837d415025e31505c769f6ef13ba0196feaea2`:
  [password CSV writer](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/password_manager/core/browser/export/password_csv_writer.cc),
  [CSV quoting writer](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/password_manager/core/browser/export/csv_writer.cc)
  and [OSCryptAsync interface](https://github.com/chromium/chromium/blob/c0837d415025e31505c769f6ef13ba0196feaea2/components/os_crypt/async/browser/os_crypt_async.h)
  were read. The CSV writer defines `name,url,username,password,note`, expands available
  affiliated domains and sorts records. A database-row dump is not automatically
  identical to the browser's grouped/affiliated credential export.

Current Brave-specific storage schemas, encryption providers and OS access compatibility
have **not** yet been fully researched or tested. No universal decryption or cross-machine
restore claim is supported by this scope review. No real user credential database or
keyring was opened during this review.

## Item 1: user edits and approved automation investigation

Review proceeds one item at a time. The user edited item 1: credential values may be
read only after an explicit backup/export operation including Passwords, never during
startup, discovery, navigation or category selection. Request OS/keyring authentication
when supported, with the user entering credentials into the native authentication UI.
An application explanation can precede that dialog; BraveBackup must not retain OS
credentials. Authentication cancellation/failure stops credential access. A successful
OS login is not a guarantee that every browser encryption provider permits access.

The user asked whether Selenium could instead launch Brave in the background and
automate its official CSV export. The user approved investigating this as a fallback,
potentially with visible authentication; no implementation route is verified yet.
[ChromeDriver profile/binary options](https://developer.chrome.com/docs/chromedriver/capabilities)
and [Selenium's Chromium support](https://www.selenium.dev/documentation/webdriver/browsers/chrome/)
establish browser automation capabilities, not a supported Brave password-export API.
Chromium's [export delegate](https://chromium.googlesource.com/chromium/src/+/0a788fbaedd0baea22281a1733008144abb6634a/chrome/browser/extensions/api/passwords_private/passwords_private_delegate_impl.cc)
explicitly requests export reauthentication. Native authentication/file dialogs and
version-specific internal pages require compatibility testing and can require visible
user interaction; unattended/headless export cannot be promised.

Launching a browser with the original profile can write that profile, conflicting with
the approved read-only backup policy. An isolated copy avoids those original-profile
writes but is not guaranteed to retain access to OS/app-bound credentials. These are
design constraints to resolve before selecting a browser-assisted fallback. No browser
was launched, profile copied, password read or automation package installed for this review.

## Decisions recorded during individual review

These decisions supersede conflicting wording in the original proposal table below.

- **Item 2 kept:** Protect passwords is checked by default and requires archive encryption.
  Disabling archive encryption does not silently select CSV; Reset restores protection.
- **Item 3 kept:** explicitly selected CSV may be included inside encrypted or unencrypted
  backups, or exported separately per profile, with a fresh destination-specific warning
  and confirmation. The warning describes the actual encrypted/plaintext output.
- **Item 4 kept:** saved login entries and supported notes are included. Cookies, passkeys,
  OS keys, payment data, general autofill and cloud-only credentials are excluded.
- **Item 5 revised and accepted:** allow direct capture while Brave is running when a
  consistent read-only SQLite snapshot, including applicable WAL data, is possible.
  Otherwise ask the user to close Brave and retry. Authentication remains separate.
- **Item 6 edited:** if corruption, unsupported format/protection, failed unlocking or
  unreadable credential contents prevent portable capture, explain the issue and offer
  explicit preservation of available password-data files as-is in the backup store.
  Do not silently substitute raw files: label the result as raw/native preservation,
  record that portable password capture did not complete, and make no portability or
  recoverability guarantee. Keep the protection setting and save consent requirements.
  If the files themselves cannot be read/copied, explain that preservation is unavailable.
  Research identifies required database companions; do not infer authorization to include
  OS key stores. Missing files cannot be preserved. An inconsistent live source requires
  the close-and-retry path. This fallback belongs to Backup, not a fabricated CSV export.
- **Item 7 kept:** show profile/credential counts, capture results and any explicitly
  selected raw-file fallback without sites, usernames, passwords, notes or keys in
  reports. Cancellation/failure preserves existing destinations and cleans temporary output.

## Original proposal table (subject to recorded revisions above)

| Choice                                        | Proposed behavior                                                                                                                                                                                                                                                                                                                                                                                                 |
|-----------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Source method — recommended automatic capture | Read supported local password records directly from the selected profiles, using a stable read-only database snapshot and OS-supported key access under the current user. Research and fixtures establish supported Brave versions/providers first. Inaccessible OS-bound/app-bound variants fail explicitly; no manual-file substitution is made silently.                                                       |
| Protect passwords                             | Add a checkbox, checked by default when Passwords is selected. Checked means a portable credential payload saved only inside an encrypted archive, without plaintext staging. Turning off whole-archive encryption does not uncheck this control or authorize plaintext; explain that the user must re-enable encryption or explicitly choose CSV. Reset restores protection.                                     |
| Optional CSV                                  | Unchecking protection explicitly selects Brave-compatible CSV inside the backup, including an unencrypted backup if chosen. Also add password CSV export in the Export tab, with a separate destination per selected profile. No credential preview is shown. A fresh warning/confirmation precedes every local CSV save and identifies its destination.                                                          |
| Contents                                      | Preserve supported saved website/app login names, URLs/realms, usernames, passwords and stored notes. CSV compatibility includes the researched columns, encoding and quoting; browser-specific grouping/affiliation behavior must be documented and tested before claiming identical results. Cookies, passkeys, OS keys, payment data, cloud-only records and general form autofill are outside this increment. |
| Capture condition and scope                   | Apply to all selected profiles with Brave observed closed. Do not launch/close Brave or modify a profile during capture. OS-supported user/keyring prompts may be required; the application never asks the user to paste OS keys.                                                                                                                                                                                 |
| Completeness                                  | Missing/corrupt databases, inaccessible required keys, unsupported schemas/encryption or unreadable selected credentials block the operation; nothing is silently omitted or replaced by encrypted-file-only success. A validated empty saved-password collection may report zero. Credential types excluded from scope are reported separately without revealing contents.                                       |
| Feedback                                      | Report safe profile/credential counts and capability limits, without sites, usernames, passwords, notes or keys in logs/reports. Keep credentials out of screenshots/fixtures; use synthetic test records only. Cancellation/failure preserves existing destinations and cleans temporary output.                                                                                                                 |

For the source method, the alternative is **manual official CSV input first**: the user
exports from Brave, then explicitly assigns a CSV file to each selected profile. This is
a different workflow and requires approval rather than being substituted for automatic
capture. The app cannot prove an arbitrary file's profile origin, and encrypting its copy
does not protect the already-existing plaintext source file. Do not delete that file
automatically. This alternative avoids direct OS decryption but does not fulfill automatic
capture by itself.

## Protection and consent matrix

| Password choice          | Archive encryption     | Result                                                                                                       |
|--------------------------|------------------------|--------------------------------------------------------------------------------------------------------------|
| Protect checked          | On                     | Protected portable credential payload; no plaintext file or database staging.                                |
| Protect checked          | Off                    | Block with an explanation; never automatically switch to CSV.                                                |
| CSV explicitly chosen    | On                     | CSV inside encrypted archive, after confirmation explaining it becomes readable after decryption/extraction. |
| CSV explicitly chosen    | Off                    | Readable CSV inside unencrypted archive, after the anyone-with-file-access warning and confirmation.         |
| Standalone CSV in Export | No encrypted container | Explicit plaintext choice plus the anyone-with-file-access warning and confirmation for that destination.    |

Encrypted-container warnings must describe their actual protection; do not falsely say
that an encrypted archive is readable by anyone who merely has its file. Consent is never
cached across operations. Selecting Passwords or disabling archive encryption alone does
not authorize a plaintext save. Existing approved unencrypted CSV permission is retained.

## Phased completion after scope approval

1. Research/fixture checkpoint: pin Brave/Chromium schemas and export semantics, define
   a truthful Windows/Linux support matrix, consistent SQLite/WAL capture, key access and
   unsupported behavior. Explain any SQLite or platform-library dependency before adding it.
   No protection change or package installation is inferred from an unsupported provider.
2. Implement and test bounded read-only credential capture with OS work in Platform,
   Brave formats in Backup and browser-independent payload contracts in Core. No real
   credentials in tests or diagnostic output. Review limitations before enabling capture.
3. Implement the protected portable payload and optional CSV serializer with quoting,
   Unicode, embedded separators/newlines, notes, emptiness and completeness fixtures.
   Document any new archive version/capability while preserving reads of versions 1–5.
4. Wire Backup protection controls and Export destinations/consent, then test encrypted
   and explicitly unencrypted workflows, overwrite races, cancellation, cleanup and
   unchanged profile data. Review actual UI controls and completion evidence.
5. Return to the separately deferred restore/import, Backups and Profiles/Merge/Sync reviews.

Scope review is complete. The next phase is the research/fixture checkpoint above,
including the approved browser-assisted fallback investigation and raw-file preservation
requirements. Existing working backup categories and bookmark exports remain unchanged.
