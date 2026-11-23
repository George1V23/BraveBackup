#!/usr/bin/env python3
"""Exercise a synthetic v43 write in native Brave on a private Linux keyring."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import secrets
import select
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
import traceback


class ProbeFailure(Exception):
    pass


def require(condition, message):
    if not condition:
        raise ProbeFailure(message)


@contextlib.contextmanager
def browser(binary, profile):
    # Chrome DevTools Protocol uses the child's descriptors 3 and 4.
    command = [
        "/bin/sh", "-c", 'exec 3<&0 4>&1 </dev/null >/dev/null; exec "$@"', "brave-probe",
        binary, "--headless", "--remote-debugging-pipe", "--no-first-run",
        "--disable-background-networking", "--disable-component-update",
        "--disable-sync", "--disable-breakpad", "--password-store=gnome-libsecret",
        "--host-resolver-rules=MAP * ~NOTFOUND", f"--user-data-dir={profile}", "about:blank",
    ]
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL) as process:
        buffered = bytearray()
        request_id = 0

        def call(method, params=None, session=None):
            nonlocal request_id
            request_id += 1
            request = {"id": request_id, "method": method, "params": params or {}}
            if session is not None:
                request["sessionId"] = session
            process.stdin.write(json.dumps(request).encode() + b"\0")
            process.stdin.flush()
            deadline = time.monotonic() + 15
            while True:
                if b"\0" in buffered:
                    end = buffered.index(0)
                    response = json.loads(buffered[:end])
                    del buffered[:end + 1]
                    if response.get("id") == request_id:
                        require("error" not in response, f"DevTools {method} failed.")
                        return response["result"]
                else:
                    remaining = deadline - time.monotonic()
                    require(remaining > 0 and select.select([process.stdout], [], [], remaining)[0],
                            f"DevTools {method} timed out.")
                    chunk = os.read(process.stdout.fileno(), 65536)
                    require(chunk, "Brave closed its DevTools pipe.")
                    buffered.extend(chunk)

        try:
            version = call("Browser.getVersion")["product"]
            target = call("Target.createTarget", {"url": "chrome://password-manager/passwords"})["targetId"]
            session = call("Target.attachToTarget", {"targetId": target, "flatten": True})["sessionId"]

            def evaluate(expression):
                result = call("Runtime.evaluate", {"expression": expression, "awaitPromise": True,
                                                   "returnByValue": True}, session)
                require("exceptionDetails" not in result, "Password Manager evaluation failed.")
                return result["result"].get("value")

            for _ in range(100):
                if evaluate("typeof chrome?.passwordsPrivate?.getSavedPasswordList === 'function'"):
                    break
                time.sleep(0.1)
            else:
                raise ProbeFailure("Password Manager API unavailable in this Brave build.")
            yield version, evaluate
            call("Browser.close")
            require(process.wait(timeout=15) == 0, "Brave did not exit cleanly.")
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


def crypt(key, value, decrypt=False):
    # The key is generated for this probe's disposable Secret Service only.
    args = ["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", (b" " * 16).hex()]
    if decrypt:
        args.append("-d")
    return subprocess.run(args, input=value, capture_output=True, check=True, timeout=10).stdout


def check_entry(evaluate, username, password, note):
    expected = json.dumps([username, password, note])
    require(evaluate(f"""(async () => {{
        const [username, password, note] = {expected};
        const api = chrome.passwordsPrivate;
        for (let attempt = 0; attempt < 100; attempt++) {{
            const entries = (await api.getSavedPasswordList()).filter(entry => entry.username === username);
            if (entries.length === 1 && entries[0].storedIn === 'DEVICE') {{
                const [details] = await api.requestCredentialsDetails([entries[0].id]);
                return details?.password === password && (details?.note ?? '') === note;
            }}
            await new Promise(resolve => setTimeout(resolve, 100));
        }}
        return false;
    }})()"""), "Brave did not return the expected synthetic profile login and note.")


def probe(binary, root):
    require(Path(os.readlink(f"/proc/{os.getppid()}/exe")).name == "dbus-run-session",
            "Expected a fresh private D-Bus session.")
    require(Path.home() == root and os.environ["XDG_RUNTIME_DIR"] == str(root / "runtime"),
            "Expected an isolated home and runtime directory.")

    require(root.is_absolute() and root.resolve() == root and
            set(root.iterdir()) == {root / "runtime"}, "Expected an unused temporary root.")
    profile = root / "browser"
    profile.mkdir(mode=0o700)

    subprocess.run(["gnome-keyring-daemon", "--unlock"], input=secrets.token_hex(24).encode(),
                   capture_output=True, check=True, timeout=10)
    secret = secrets.token_hex(24).encode()
    subprocess.run(["secret-tool", "store", "--label=BraveBackup synthetic fixture", "application", "brave"],
                   input=secret, capture_output=True, check=True, timeout=10)
    key = hashlib.pbkdf2_hmac("sha1", secret, b"saltysalt", 1, 16)

    origin = "https://password-restore.example.invalid/"
    seed = {"url": origin, "username": "native-seed", "password": "native synthetic ✓",
            "note": "native synthetic note ✓", "useAccountStore": False}
    with browser(binary, profile) as (version, evaluate):
        evaluate("chrome.passwordsPrivate.addPassword(" + json.dumps(seed) + ")")
        check_entry(evaluate, seed["username"], seed["password"], seed["note"])

    database = profile / "Default" / "Login Data"
    require(database.is_file(), "Brave did not create a local profile store.")
    require_clean_sidecars(database)

    restored_password = "restored synthetic 🔒"
    restored_note = "restored synthetic note ✓"
    staged = root / "staged.db"
    original_hash = digest(database)
    shutil.copyfile(database, staged)
    with contextlib.closing(sqlite3.connect(staged.as_uri() + "?mode=rw", uri=True)) as db, db:
        schema = dict(db.execute("SELECT key,value FROM meta WHERE key IN ('version','last_compatible_version')"))
        require(schema == {"version": "43", "last_compatible_version": "40"},
                "This Brave build does not use schema 43 with compatible version 40.")
        db.execute("PRAGMA foreign_keys=ON")
        native = db.execute("""
            SELECT password_value, CAST(password_notes.value AS BLOB), password_notes.key,
                   logins.date_created, typeof(logins.password_value), typeof(password_notes.value)
            FROM logins JOIN password_notes ON password_notes.parent_id = logins.id
            WHERE username_value = ?
            """, (seed["username"],)).fetchone()
        require(native is not None, "Native seed login and note were not stored.")
        require(native[4:] == ("blob", "text"), "Unexpected native encrypted field storage classes.")
        for blob, expected in zip(native[:2], (seed["password"], seed["note"])):
            require(blob.startswith(b"v11") and crypt(key, blob[3:], decrypt=True) == expected.encode(),
                    "Native Brave did not use the isolated Secret Service v11 key for both values.")
        require(native[2] == "", "Native Brave used an unexpected user note key.")
        require(not db.execute("SELECT 1 FROM sync_entities_metadata UNION ALL SELECT 1 FROM sync_model_metadata").fetchall(),
                "Unexpected Sync metadata in the disposable store.")

        # Deliberately retain a blocked origin and a stats row across the replacement.
        blocked = "https://blocked.example.invalid/"
        db.execute("""
            INSERT INTO logins(origin_url, signon_realm, date_created, blacklisted_by_user, scheme)
            VALUES (?, ?, ?, 1, 0)
            """, (blocked, blocked, native[3]))
        db.execute("INSERT INTO stats VALUES (?, 'fixture-stats', 3, ?)", (origin, native[3]))
        db.execute("""
            INSERT INTO insecure_credentials(parent_id, insecurity_type, create_time)
            SELECT id, 0, date_created FROM logins WHERE username_value = ?
            """, (seed["username"],))

        verify_form_collision(db, seed["username"])
        delete_saved_logins(db)
        require(db.execute("SELECT COUNT(*) FROM logins WHERE blacklisted_by_user = 1").fetchone() == (1,) and
                db.execute("SELECT dismissal_count FROM stats").fetchall() == [(3,)],
                "Replacement changed unrelated synthetic rows.")
        write_login(db, key, origin, "restored", restored_password, restored_note, native[3])
        write_login(db, key, origin, "restored-empty", "", "", native[3])
        verify_database(db)

    require_clean_sidecars(staged)
    require_clean_sidecars(database)
    require(digest(database) == original_hash, "Preparing the stage changed the original synthetic database.")
    os.replace(staged, database)
    with browser(binary, profile) as (_, evaluate):
        check_entry(evaluate, "restored", restored_password, restored_note)
        check_entry(evaluate, "restored-empty", "", "")
        require_saved_users(evaluate, ["restored", "restored-empty"])

    require_clean_sidecars(database)
    shutil.copyfile(database, staged)
    with contextlib.closing(sqlite3.connect(staged.as_uri() + "?mode=rw", uri=True)) as db, db:
        db.execute("PRAGMA foreign_keys=ON")
        delete_saved_logins(db)
        require(db.execute("SELECT blacklisted_by_user FROM logins").fetchall() == [(1,)] and
                db.execute("SELECT dismissal_count FROM stats").fetchall() == [(3,)],
                "Empty replacement changed unrelated synthetic rows.")
        verify_database(db)

    require_clean_sidecars(staged)
    require_clean_sidecars(database)
    os.replace(staged, database)
    with browser(binary, profile) as (_, evaluate):
        require_saved_users(evaluate, [])
    print(f"PASS {version}: v43/40, Secret Service v11, staged replacement and empty-store reopen.")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").digest()


def require_clean_sidecars(database):
    for suffix in ("-wal", "-shm", "-journal"):
        sidecar = database.with_name(database.name + suffix)
        require(not sidecar.exists() or sidecar.stat().st_size == 0,
                f"The disposable database has a nonempty {suffix} sidecar after shutdown.")


def verify_form_collision(db, username):
    db.execute("UPDATE logins SET username_element = 'form-a' WHERE username_value = ?", (username,))
    db.execute("""
        INSERT INTO logins(origin_url, username_element, username_value, password_element, password_value,
                           signon_realm, date_created, blacklisted_by_user, scheme)
        SELECT origin_url, 'form-b', username_value, password_element, password_value,
               signon_realm, date_created, blacklisted_by_user, scheme
        FROM logins WHERE username_value = ?
        """, (username,))
    try:
        db.execute("UPDATE logins SET username_element = '' WHERE username_value = ?", (username,))
    except sqlite3.IntegrityError as error:
        require(error.sqlite_errorname == "SQLITE_CONSTRAINT_UNIQUE", "Unexpected collision error.")
    else:
        raise ProbeFailure("Distinct native form keys collapsed without a uniqueness error.")
    require(db.execute("""
        SELECT username_element FROM logins WHERE username_value = ? ORDER BY username_element
        """, (username,)).fetchall() == [("form-a",), ("form-b",)], "Collision changed the staged rows.")


def write_login(db, key, origin, username, password, note, created):
    password_blob = b"v11" + crypt(key, password.encode()) if password else b""
    note_blob = b"v11" + crypt(key, note.encode()) if note else b""
    login_id = db.execute("""
        INSERT INTO logins(origin_url, username_element, username_value, password_element, password_value,
                           signon_realm, date_created, blacklisted_by_user, scheme, password_type, federation_url)
        VALUES (?, '', ?, '', ?, ?, ?, 0, 0, 0, '')
        """, (origin, username, password_blob, origin, created)).lastrowid
    db.execute("""
        INSERT INTO password_notes(parent_id, key, value, date_created, confidential)
        VALUES (?, '', CAST(? AS TEXT), ?, 0)
        """, (login_id, note_blob, created))


def delete_saved_logins(db):
    db.execute("""
        DELETE FROM logins WHERE blacklisted_by_user = 0 AND scheme = 0
        AND (federation_url IS NULL OR federation_url IN ('', 'null'))
        """)
    require(not db.execute("SELECT 1 FROM password_notes UNION ALL SELECT 1 FROM insecure_credentials").fetchall(),
            "Related rows from deleted synthetic logins were retained.")


def verify_database(db):
    require(db.execute("PRAGMA integrity_check").fetchall() == [("ok",)] and
            not db.execute("PRAGMA foreign_key_check").fetchall(),
            "The staged synthetic store failed SQLite verification.")


def require_saved_users(evaluate, users):
    require(evaluate(f"""(async () => {{
        const expected = {json.dumps(users)};
        const api = chrome.passwordsPrivate;
        const entries = await api.getSavedPasswordList();
        return JSON.stringify(entries.map(entry => entry.username).sort()) === JSON.stringify(expected);
    }})()"""), "Brave did not return the expected synthetic saved-login inventory.")


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--private-session":
        probe(sys.argv[2], Path(sys.argv[3]))
        return

    require(len(sys.argv) == 2, "Usage: python3 probe.py /absolute/path/to/brave-binary")
    binary = Path(sys.argv[1])
    require(binary.is_absolute() and binary.is_file() and os.access(binary, os.X_OK),
            "Provide an absolute path to an executable Brave binary.")
    for tool in ("dbus-run-session", "gnome-keyring-daemon", "secret-tool", "openssl"):
        require(shutil.which(tool, path=os.defpath), f"Required system executable not found: {tool}.")
    with tempfile.TemporaryDirectory(prefix="brave-password-restore-") as temporary:
        root = Path(temporary)
        (root / "runtime").mkdir(mode=0o700)
        environment = {"PATH": os.defpath, "HOME": str(root), "XDG_RUNTIME_DIR": str(root / "runtime"),
                       "XDG_CONFIG_HOME": str(root / "config"), "XDG_DATA_HOME": str(root / "data"),
                       "XDG_CACHE_HOME": str(root / "cache"), "XDG_STATE_HOME": str(root / "state")}
        command = ["dbus-run-session", "--", sys.executable, str(Path(__file__).resolve()),
                   "--private-session", str(binary), str(root)]
        with subprocess.Popen(command, env=environment, stderr=subprocess.DEVNULL,
                              start_new_session=True) as process:
            try:
                exit_code = process.wait(timeout=100)
            except subprocess.TimeoutExpired:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(process.pid, signal.SIGKILL)
                raise ProbeFailure("The disposable D-Bus session timed out.") from None
        if exit_code:
            raise SystemExit(exit_code)


if __name__ == "__main__":
    try:
        main()
    except ProbeFailure as error:
        print(str(error), flush=True)
        raise SystemExit(1) from None
    except (OSError, sqlite3.Error, subprocess.SubprocessError) as err:
        line = traceback.extract_tb(err.__traceback__)[-1].lineno
        code = getattr(err, "sqlite_errorname", type(err).__name__)
        print(f"A native tool or SQLite operation failed ({code}, line {line}).", flush=True)
        raise SystemExit(1) from None
