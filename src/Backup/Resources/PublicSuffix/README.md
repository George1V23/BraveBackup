# Pinned Public Suffix List data

`effective_tld_names.dat` is an unmodified copy from Chromium revision
`b8c2be851f62f6a394ec57955d7f31335d7cb6d4`:
https://github.com/chromium/chromium/blob/b8c2be851f62f6a394ec57955d7f31335d7cb6d4/net/base/registry_controlled_domains/effective_tld_names.dat

SHA-256: `d4d5c24befb4a21306ace7f28cb04d35121d90f300f8c56c442937f9d1b5e278`

The list is Mozilla Public License 2.0 data; see `LICENSE-MPL-2.0.txt` and the
notices retained at the top of the original file. Original upstream project/source:
https://publicsuffix.org/list/public_suffix_list.dat . This pinned copy is embedded
for offline matching against the researched Chromium revision, rather than fetching
an unpredictable runtime list. Both ICANN and private rules are included.

This notice and the full license are copied to build/publish output. The source-code
form of the data is available in this repository at
`src/Backup/Resources/PublicSuffix/effective_tld_names.dat` and at the exact upstream
revision linked above. No NuGet package or browser network service was added.

The ASCII-only matcher rejects IDN/punycode hosts and skips Unicode-only rules;
complete Unicode/Chromium IDN canonicalization remains unsupported. Changing the
pinned data requires updating provenance, expected hash and compatibility tests.
This does not claim equality with another browser release's PSL snapshot.
