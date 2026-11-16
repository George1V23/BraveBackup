"""Independent synthetic v5 ZIP producer; no application serializer is used."""
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent
stored = json.loads((root / "storage/table-and-wal.expected.json").read_text())["value"]
installation = "11111111-1111-1111-1111-111111111111"
profile = "22222222-2222-2222-2222-222222222222"
path = f"installations/{installation}/profiles/{profile}/tabmanagerplus/saved-windows-v1.json"
payload = {
    "schemaVersion": 1, "captureKind": "extension-saved-windows",
    "capturedUtc": "2026-11-17T00:00:00+00:00",
    "extensionId": "cnkdjjdmfiffagllbiiilooaoofcoeff", "extensionVersion": "7.0.0",
    "source": "local-extension-settings", "sourceFileCount": 5,
    "windows": list(stored.values()),
}
data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
manifest = {
    "format": "BraveBackupArchive", "formatVersion": 5,
    "archiveId": "33333333-3333-3333-3333-333333333333",
    "createdUtc": "2026-11-17T00:00:00+00:00", "applicationVersion": "independent-fixture",
    "requiredCapabilities": ["tabmanagerplus-v1"], "compression": "none",
    "installations": [{"id": installation, "product": "Brave", "channel": "Stable",
                       "browserVersion": "fixture", "profiles": [{
                           "id": profile, "displayName": "Synthetic", "directoryName": "Default",
                           "categories": [{"id": "tabmanagerplus", "version": 1, "files": [{
                               "path": path, "role": "portable-extension-windows", "length": len(data),
                               "sha256": hashlib.sha256(data).hexdigest()}]}]}]}],
}
with zipfile.ZipFile(root / "Archive.v5.tabmanagerplus.bravebackup", "w") as archive:
    for name, contents in [(path, data), ("manifest.json", json.dumps(manifest).encode())]:
        entry = zipfile.ZipInfo(name, (2026, 11, 17, 0, 0, 0))
        entry.create_system = 0
        entry.external_attr = 0
        archive.writestr(entry, contents)
