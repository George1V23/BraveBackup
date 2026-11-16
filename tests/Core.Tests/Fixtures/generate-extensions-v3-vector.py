"""Generate a synthetic raw-extension archive-v3 ZIP independently of the C# writer.
The opaque binary marker is not a fabricated LevelDB database or a recovery fixture.
"""
from pathlib import Path
import hashlib
import json
import zipfile
root = Path(__file__).parent
installation = '11111111-1111-4111-8111-111111111111'
profile = '22222222-2222-4222-8222-222222222222'
extension = 'a' * 32
prefix = f'installations/{installation}/profiles/{profile}/extensions/'
payloads = []
references = []
versions = []
for version in ['10.0', '9.0']:
    directory = version + '_0'
    versions.append(dict(directory=directory, version=version, name='Fixture'))
    data = json.dumps(dict(name='Fixture', version=version, manifest_version=3)).encode()
    payloads.append(('package', directory + '/manifest.json', data))
    payloads.append(('package', directory + '/script.js', b'// synthetic fixture only\n'))
payloads.append(('local', 'opaque-test.bin', bytes([0, 255, 128, 13, 10, 0, 5])))
files = []
for area, relative, data in payloads:
    path = prefix + 'data/' + extension + '/' + area + '/' + relative
    digest = hashlib.sha256(data).hexdigest()
    files.append(dict(path=path, role='extension-file', length=len(data), sha256=digest))
    references.append(dict(area=area, relativePath=relative, archivePath=path, length=len(data), sha256=digest))
inventory = dict(schemaVersion=1, extensions=[dict(id=extension, versions=versions,
    localStorage='present', syncStorage='empty', directories=['package/10.0_0', 'package/9.0_0', 'local', 'sync'], files=references)])
inventory_bytes = json.dumps(inventory, indent=2).encode()
files.insert(0, dict(path=prefix + 'inventory-v1.json', role='extension-inventory', length=len(inventory_bytes), sha256=hashlib.sha256(inventory_bytes).hexdigest()))
manifest = dict(format='BraveBackupArchive', formatVersion=3, archiveId='33333333-3333-4333-8333-333333333333',
    createdUtc='2026-11-16T00:00:00+00:00', applicationVersion='fixture', requiredCapabilities=['extensions-v1'], compression='balanced',
    installations=[dict(id=installation, product='Browser', channel='Stable', browserVersion='fixture', profiles=[dict(id=profile,
        displayName='Fixture', directoryName='Default', categories=[dict(id='extensions', version=1, files=files)])])])
with zipfile.ZipFile(root / 'Archive.v3.extensions.bravebackup', 'w') as archive:
    entries = [(row['path'], data) for row, (_, _, data) in zip(files[1:], payloads)]
    entries += [(prefix + 'inventory-v1.json', inventory_bytes), ('manifest.json', json.dumps(manifest, indent=2).encode())]
    for name, data in entries:
        entry = zipfile.ZipInfo(name, date_time=(2026, 11, 16, 0, 0, 0)); entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, data)
print('Generated independent extension-v3 ZIP fixture.')
