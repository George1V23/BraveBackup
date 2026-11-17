# Linux Secret Service v11 key-access checkpoint

`BraveLinuxPasswordKey.Unlock` performs an explicit existing-secret lookup for
`application=brave`, derives the [researched v11 key](passwords-decoding.md), and clears
the returned secret buffer on success, cancellation or failure. Its injectable lookup
must return owned bytes and retain no copies. A genuinely present empty value remains
distinct from a missing item. It never substitutes an empty secret or another backend.

`LinuxSecretService.LookupExisting` belongs to Platform and uses the native libsecret,
GLib, GObject and GIO runtimes. No NuGet dependency was added. Missing native libraries
produce an explicit unsupported-platform failure. Distribution packaging must provide
`libsecret-1.so.0`, `libglib-2.0.so.0`, `libgobject-2.0.so.0`, `libgio-2.0.so.0` and a
working session Secret Service. No installation or service configuration is performed.

## Lookup, prompting and ownership

The implementation uses a private libsecret service proxy, reads the `default` collection
alias and searches only that collection for the supplied application attribute. The
Brave-specific identity stays in Backup. This follows the pinned
[Chromium Secret Service provider](https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/components/os_crypt/async/browser/freedesktop_secret_key_provider.cc)
and [Brave's identity patch](https://github.com/brave/brave-core/blob/a9f619b0c086e98678e89caf332b5badb1be1cea/patches/components-os_crypt-async-browser-freedesktop_secret_key_provider.h.patch).
Unlike browser initialization, missing items never cause key or collection creation.
Multiple matching items fail explicitly rather than selecting an uncertain key.

[Collection search](https://gnome.pages.gitlab.gnome.org/libsecret/method.Collection.search_sync.html)
uses ALL, UNLOCK and LOAD_SECRETS. A locked keyring may show its native prompt; an already
unlocked keyring may need none. Unlock cancellation/failure can leave a matching locked
item without a secret, which is rejected. BraveBackup never collects OS passwords or
bypasses protection. The operation must run on a worker because native calls can block;
a GCancellable forwards cancellation to pending native calls. The adapter creates only
a session/proxy and may unlock an existing item; it never stores, edits or deletes items.

A [private service proxy](https://gnome.pages.gitlab.gnome.org/libsecret/type_func.Service.open_sync.html)
avoids adding loaded item secrets to libsecret's shared service-proxy cache. Item, value,
collection, service, list, attribute-table, error and cancellable references are released
on exit. Native values are owned/released through libsecret, not overwritten through a
borrowed pointer. Secret bytes use explicit lengths, not null termination, and are bounded
to 64 KiB. Managed result buffers are cleared on failure; successful direct adapter
callers own and must clear the result. Native error messages are discarded rather than
copied into diagnostics.

## Validation and remaining integration

Seven key-wrapper tests verify the exact Brave identity, the independent Linux vector,
owned-buffer cleanup, missing versus present-empty behavior, safe errors, size limits
and cancellation. Five native tests use a fresh private D-Bus and a synthetic lookup-only
service, covering successful lookup, binary/empty secrets, missing/ambiguous/locked and
oversized items, and in-flight cancellation. Run them with:

```sh
dbus-run-session -- python3 tests/Linux.Tests/Fixtures/run-secret-service-tests.py
```

The launcher requires Python dbus/gi, dbus-run-session and the native runtimes, verifies
its parent is dbus-run-session, and runs only the native fixture tests. Four fixture-only
tests are skipped in ordinary test runs; the pre-cancelled validation test runs normally
without contacting any service. The fixture rejects unimplemented methods and exposes
only synthetic values. No real profile, desktop keyring or credentials are accessed.

Real keyring prompt interaction and real Brave compatibility still need validation.
This supports the demonstrated Secret Service path only: direct KWallet, secret portal
v12, POSIX/basic v10 and app-bound providers remain unsupported. Operation orchestration
must establish provider context, explain native authentication limits and offer the
approved raw-file-or-cancel fallback. Password saving, CSV consent, archive integration
and the Passwords UI remain pending; existing archive formats are unchanged.
