"""Regenerate test-only v1 vectors with Python ZIP/hashlib and OpenSSL via ctypes.
Fixed salts/nonces are for interoperability tests only; never use them in production.
No third-party Python packages are needed. Run from this file's directory.
"""
import ctypes as c
import ctypes.util
import hashlib
import json
import struct
import zipfile
from pathlib import Path

root = Path(__file__).parent
lib = c.CDLL(ctypes.util.find_library('crypto'))
lib.EVP_CIPHER_CTX_new.restype = c.c_void_p
lib.EVP_CIPHER_CTX_free.argtypes = [c.c_void_p]
lib.EVP_aes_256_gcm.restype = c.c_void_p
lib.EVP_EncryptInit_ex.argtypes = [c.c_void_p, c.c_void_p, c.c_void_p, c.c_void_p, c.c_void_p]
lib.EVP_EncryptUpdate.argtypes = [c.c_void_p, c.c_void_p, c.POINTER(c.c_int), c.c_void_p, c.c_int]
lib.EVP_EncryptFinal_ex.argtypes = [c.c_void_p, c.c_void_p, c.POINTER(c.c_int)]
lib.EVP_CIPHER_CTX_ctrl.argtypes = [c.c_void_p, c.c_int, c.c_int, c.c_void_p]

def encrypt(key, nonce, data, aad):
    ctx = lib.EVP_CIPHER_CTX_new()
    count = c.c_int()
    output = c.create_string_buffer(len(data) + 16)
    final = c.create_string_buffer(16)
    tag = c.create_string_buffer(16)
    try:
        assert lib.EVP_EncryptInit_ex(ctx, lib.EVP_aes_256_gcm(), None, key, nonce) == 1
        assert lib.EVP_EncryptUpdate(ctx, None, c.byref(count), aad, len(aad)) == 1
        assert lib.EVP_EncryptUpdate(ctx, output, c.byref(count), data, len(data)) == 1
        length = count.value
        assert lib.EVP_EncryptFinal_ex(ctx, final, c.byref(count)) == 1
        assert count.value == 0
        assert lib.EVP_CIPHER_CTX_ctrl(ctx, 0x10, 16, tag) == 1  # EVP_CTRL_GCM_GET_TAG
        return output.raw[:length] + tag.raw
    finally:
        lib.EVP_CIPHER_CTX_free(ctx)

header = b'BBKENC1\n' + struct.pack('<HBBII', 1, 1, 1, 600000, 1048576) + bytes(range(16)) + bytes(range(16, 24)) + bytes(4)
assert len(header) == 48
password = 'vector-only-Îñ☃'
plaintext = 'Independent archive envelope vector: Îñ☃\n'.encode()
key = hashlib.pbkdf2_hmac('sha256', password.encode(), header[20:36], 600000, 32)
output = bytearray(header)
for index, data in enumerate([plaintext, b'']):
    nonce = header[36:44] + struct.pack('>I', index)
    aad = header + struct.pack('<II', index, len(data))
    output.extend(struct.pack('<I', len(data)) + encrypt(key, nonce, data, aad))
(root / 'Envelope.v1.bin').write_bytes(output)
(root / 'Envelope.v1.txt').write_bytes(plaintext)

installation = '11111111-1111-4111-8111-111111111111'
profile = '22222222-2222-4222-8222-222222222222'
prefix = f'installations/{installation}/profiles/{profile}/bookmarks/'
files = []
payloads = [('source.json', 'native-source', (root / 'Bookmarks.sample.json').read_bytes()),
            ('portable-v1.json', 'portable-v1', (root / 'Backup.v1.json').read_bytes())]
for name, role, data in payloads:
    files.append(dict(path=prefix + name, role=role, length=len(data), sha256=hashlib.sha256(data).hexdigest()))
manifest = dict(format='BraveBackupArchive', formatVersion=1,
    archiveId='33333333-3333-4333-8333-333333333333', createdUtc='2026-11-16T00:00:00+00:00',
    applicationVersion='fixture', requiredCapabilities=['bookmarks-v1'], compression='balanced',
    installations=[dict(id=installation, product='Browser', channel='Stable', browserVersion='fixture',
        profiles=[dict(id=profile, displayName='Fixture', directoryName='Default',
            categories=[dict(id='bookmarks', version=1, files=files)])])])
with zipfile.ZipFile(root / 'Archive.v1.bravebackup', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name, _, data in payloads:
        entry = zipfile.ZipInfo(prefix + name, date_time=(2026, 11, 16, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, data)
    entry = zipfile.ZipInfo('manifest.json', date_time=(2026, 11, 16, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(entry, json.dumps(manifest, ensure_ascii=False, indent=2).encode())
print('Generated fixed OpenSSL envelope and Python ZIP fixtures.')
