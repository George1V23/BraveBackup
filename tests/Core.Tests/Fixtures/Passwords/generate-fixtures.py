"""Synthetic research fixtures, not a password extractor. Requires Python SQLite and OpenSSL.
No user profile/keyring is opened. Fixed keys/nonces and example.invalid values are test-only.
Run from any directory. Existing generated fixture files are intentionally regenerated.
"""
import csv
import ctypes as c
import ctypes.util
import hashlib
import io
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent
SCHEMA = (ROOT / "schema-v43.sql").read_text()
LIB = c.CDLL(ctypes.util.find_library("crypto"))
LIB.EVP_CIPHER_CTX_new.restype = c.c_void_p
LIB.EVP_CIPHER_CTX_free.argtypes = [c.c_void_p]
LIB.EVP_aes_256_gcm.restype = c.c_void_p
for op in ("Encrypt", "Decrypt"):
    getattr(LIB, f"EVP_{op}Init_ex").argtypes = [c.c_void_p] * 5
    getattr(LIB, f"EVP_{op}Update").argtypes = [c.c_void_p, c.c_void_p, c.POINTER(c.c_int), c.c_void_p, c.c_int]
    getattr(LIB, f"EVP_{op}Final_ex").argtypes = [c.c_void_p, c.c_void_p, c.POINTER(c.c_int)]
LIB.EVP_CIPHER_CTX_ctrl.argtypes = [c.c_void_p, c.c_int, c.c_int, c.c_void_p]


def gcm(key, nonce, plaintext):
    """Use OpenSSL directly, then independently reopen the tag before freezing a vector."""
    ctx = LIB.EVP_CIPHER_CTX_new()
    count = c.c_int()
    out = c.create_string_buffer(len(plaintext) + 16)
    final = c.create_string_buffer(16)
    tag = c.create_string_buffer(16)
    try:
        assert LIB.EVP_EncryptInit_ex(ctx, LIB.EVP_aes_256_gcm(), None, key, nonce) == 1
        assert LIB.EVP_EncryptUpdate(ctx, out, c.byref(count), plaintext, len(plaintext)) == 1
        ciphertext = out.raw[:count.value]
        assert LIB.EVP_EncryptFinal_ex(ctx, final, c.byref(count)) == 1
        assert LIB.EVP_CIPHER_CTX_ctrl(ctx, 0x10, 16, tag) == 1
    finally:
        LIB.EVP_CIPHER_CTX_free(ctx)
    ctx = LIB.EVP_CIPHER_CTX_new()
    try:
        assert LIB.EVP_DecryptInit_ex(ctx, LIB.EVP_aes_256_gcm(), None, key, nonce) == 1
        assert LIB.EVP_DecryptUpdate(ctx, out, c.byref(count), ciphertext, len(ciphertext)) == 1
        recovered = out.raw[:count.value]
        assert LIB.EVP_CIPHER_CTX_ctrl(ctx, 0x11, 16, tag) == 1
        assert LIB.EVP_DecryptFinal_ex(ctx, final, c.byref(count)) == 1
        assert recovered == plaintext
    finally:
        LIB.EVP_CIPHER_CTX_free(ctx)
    return b"v10" + nonce + ciphertext + tag.raw


def cbc(key, plaintext):
    """Native OpenSSL CBC/PKCS7, with the source-defined sixteen-space IV."""
    args = ["openssl", "enc", "-aes-128-cbc", "-K", key.hex(), "-iv", (b" " * 16).hex()]
    ciphertext = subprocess.run(args, input=plaintext, capture_output=True, check=True).stdout
    assert subprocess.run(args + ["-d"], input=ciphertext, capture_output=True, check=True).stdout == plaintext
    return b"v11" + ciphertext


def fresh(path):
    if path.exists():
        path.unlink()
    db = sqlite3.connect(path)
    db.executescript(SCHEMA)
    return db


def populate(db, password, note):
    insert = """INSERT INTO logins(id,origin_url,signon_realm,username_element,username_value,
      password_element,password_value,date_created,blacklisted_by_user,scheme,federation_url,display_name)
      VALUES(?,?,?,?,?,?,?,?,?,?,?,?)"""
    db.execute(insert, (1, "https://example.invalid/login", "https://example.invalid/", "user", "fixture-user",
                       "pass", password, 1, 0, 0, "", "Synthetic site"))
    db.execute(insert, (2, "https://blocked.invalid/", "https://blocked.invalid/", "", "", "", b"", 1, 1, 0, "", ""))
    db.execute(insert, (3, "https://federated.invalid/", "https://federated.invalid/", "user", "fixture-federated",
                       "", b"", 1, 0, 0, "https://identity.invalid/", ""))
    db.execute("INSERT INTO password_notes(parent_id,key,value,date_created,confidential) VALUES(?,?,?,?,?)",
               (1, "", note, 1, 0))
    db.commit()


def digest_tree(root):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.iterdir()) if p.is_file()}


def main():
    secret = b"synthetic-keyring-secret-not-real"
    linux_key = hashlib.pbkdf2_hmac("sha1", secret, b"saltysalt", 1, 16)
    win_key = bytes(range(32))
    password = 'fixture-password, "quoted"\n\u2603'.encode()
    note = 'fixture-note\r\nsecond line, "quoted"'.encode()
    # Distinct fixed nonces for the two synthetic GCM values.
    vectors = {
        "windows-v10-password": gcm(win_key, bytes(range(12)), password),
        "windows-v10-note": gcm(win_key, bytes(range(12, 24)), note),
        "linux-v11-password": cbc(linux_key, password),
        "linux-v11-note": cbc(linux_key, note),
    }
    metadata = {"testOnly": True, "windowsKeyHex": win_key.hex(), "linuxKeyHex": linux_key.hex(),
                "syntheticLinuxSecret": secret.decode(), "passwordUtf8": password.decode(), "noteUtf8": note.decode(),
                "valuesHex": {key: value.hex() for key, value in vectors.items()}}
    (ROOT / "Crypto.synthetic.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n")
    for os_name, prefix in [("windows", "windows-v10"), ("linux", "linux-v11")]:
        db = fresh(ROOT / f"LoginData.v43.{os_name}.sqlite")
        populate(db, vectors[prefix + "-password"], vectors[prefix + "-note"])
        assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        db.close()
    db = fresh(ROOT / "LoginData.v43.empty.sqlite")
    db.close()
    db = fresh(ROOT / "LoginData.unsupported.sqlite")
    db.execute("UPDATE meta SET value='999' WHERE key='version'")
    db.commit()
    db.close()
    (ROOT / "LoginData.corrupt.bin").write_bytes(b"synthetic-invalid-database-not-a-real-SQLite-file")

    # CSV's source-defined minimal quoting, UTF-8 and OS-specific line endings.
    row = ['Synthetic, "site"', 'https://example.invalid/login', 'fixture-user', password.decode(), note.decode()]
    header = ["name", "url", "username", "password", "note"]
    def quote(value):
        return '"' + value.replace('"', '""') + '"' if any(x in value for x in '\r\n",') else value
    for os_name, ending in [("linux", "\n"), ("windows", "\r\n")]:
        data = ending.join(",".join(quote(value) for value in fields) for fields in [header, row]) + ending
        assert list(csv.reader(io.StringIO(data, newline=""))) == [header, row]
        (ROOT / f"Passwords.synthetic.{os_name}.csv").write_bytes(data.encode())

    # WAL/read-transaction experiment on synthetic data only.
    with tempfile.TemporaryDirectory(prefix="brave-password-wal-") as folder:
        root = Path(folder)
        path = root / "Login Data"
        writer = fresh(path)
        populate(writer, vectors["linux-v11-password"], vectors["linux-v11-note"])
        writer.execute("PRAGMA journal_mode=WAL")
        writer.execute("PRAGMA wal_autocheckpoint=0")
        writer.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        reader = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)
        reader.execute("BEGIN")
        assert reader.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0] == "fixture-user"
        writer.execute("UPDATE logins SET username_value='fixture-WAL-new' WHERE id=1")
        writer.commit()
        memory = sqlite3.connect(":memory:")
        reader.backup(memory)
        assert memory.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0] == "fixture-user"
        memory.close()
        reader.rollback()
        before = digest_tree(root)
        memory = sqlite3.connect(":memory:")
        reader.backup(memory)
        assert memory.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0] == "fixture-WAL-new"
        after = digest_tree(root)
        report = {"sqliteVersion": sqlite3.sqlite_version, "pinnedReadTransactionSawOldValue": True,
                  "newSnapshotSawCommittedWalValue": True,
                  "databaseBytesUnchangedByRead": before["Login Data"] == after["Login Data"],
                  "walBytesUnchangedByRead": before["Login Data-wal"] == after["Login Data-wal"],
                  "shmBytesUnchangedByRead": before["Login Data-shm"] == after["Login Data-shm"],
                  "limitations": "Synthetic Linux observation only; not a source-write guarantee for every VFS/backend."}
        memory.close()
        reader.close()
        wal_folder = ROOT / "Wal"
        wal_folder.mkdir(exist_ok=True)
        for name in ["Login Data", "Login Data-wal", "Login Data-shm"]:
            shutil.copyfile(root / name, wal_folder / name)
        # A database-only copy loses the newer committed WAL value.
        alone = root / "database-only.sqlite"
        shutil.copyfile(path, alone)
        db = sqlite3.connect(alone.as_uri() + "?mode=ro", uri=True)
        assert db.execute("SELECT username_value FROM logins WHERE id=1").fetchone()[0] == "fixture-user"
        db.close()
        writer.close()
        (ROOT / "Wal.experiment.json").write_text(json.dumps(report, indent=2) + "\n")
    hashes = {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in sorted(ROOT.rglob("*")) if p.is_file() and p.suffix != ".py" and p.name != "sha256.json"}
    (ROOT / "sha256.json").write_text(json.dumps(hashes, indent=2) + "\n")
    print("Generated and verified synthetic schema/crypto/CSV/WAL research fixtures; no user credentials accessed.")


if __name__ == "__main__":
    main()
