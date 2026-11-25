# Live custom-Testnet verification — 2026-11-25

**Decision: production NO-GO. No main merge, push or VPS deployment.**

The user authorized synthetic testing on `https://horizon-test.tadbirandishan.com/`
with the explicitly supplied passphrase `DAYACHAIN Test Network ; May 2026`.
Generated disposable credentials stayed outside Git in Windows DPAPI-protected
files; test processes received them privately. The mnemonic pasted in chat was
not used. No Mainnet transactions were submitted.

## Fixes and local validation

Live testing exposed an SDK Buffer alias that wiped the retained S-key signer
when temporary HKDF input was cleared. The adapter now copies the bytes and
checks local signatures before checkpointing or transport. Real-SDK regression
assertions now run outside caught transport exceptions; a corrupt signer must
fail before either checkpoint or broadcast.

The host now preserves all eight protocol draft field names across Blazor's
camel-case interop serializer. A new executable host interop/rendering harness
checks this contract and rendered form label targets. Fixed a malformed title
input ID, missing shell render notifications and literal Razor string attributes
that hid records or passed incorrect selection/strategy values. HD editor inputs
stay disabled during asynchronous loading, preventing loaded values from
overwriting edits. One successful test update saved the previous values before
this fix; a separately approved subsequent update verified the intended values.

The complete worktree release runner passed with 241 Node regressions and zero
build warnings/errors, including engine, crypto/journal, API HTTP, host interop,
dependency audit and published PWA integrity checks. See ignored evidence log
`artifacts/release-review/final-worktree-verification-2026-11-25.log`.
This worktree result does not attest a final immutable release image.

## Live results

Both strategies completed browser create, fresh-session recovery, update and
delete using synthetic records. S-key exact-history comparisons checked signed
XDR, historical ciphertext integrity and approved decrypted fields for all three
mutations; a deliberately mismatched draft was rejected after deletion. These
diagnostics did not fabricate or confirm a queue entry.

| Strategy / operation | Transaction hash | Ledger |
| --- | --- | --- |
| S-key create | `44cf67359a223f3270c312f2de1008354808c3521c8ab7df22c3b5174ffed660` | 3034005 |
| S-key update | `ebcc57a6c9a82f4e59fe651a3f378e708e9fa8b3eab77da5334229d928e08566` | 3034084 |
| S-key delete | `00b1ef2e67c6c644549a5d23ac4a3dbdcb20ea235f342376d721713c8d6d9d07` | 3034106 |
| HD create final manifest | `d32d55af0e614bb359726aae59467f70ceeed5d5ee5e66d94ab44ab4236899df` | 3034180 |
| HD corrected update manifest | `b0d9dfde2607eb985e63e9e3d09f9cf80a523ef9140cab54dd08bb45ae405233` | 3038315 |
| HD delete manifest operation | `c6a9cf1bbf995eee2a3d5432f40bc3aa45b0f83878ce26f725457450468f8206` | 3038345 |

HD writes span multiple transactions: these manifest hashes do not represent
all fees or prove every chunk by themselves. Current unlocked-session SEP-5
root/child derivation and authenticated registry lineage passed after fresh
recovery. Full HD historical publication verification was not exercised end to
end. No HD queued mutation was confirmed.

After deletion the HD child had zero data entries, not a retained tombstone;
its sponsored account and root registry remain. The replacement S-key account
also had zero data entries. Last observed balances were HD root 8.8992700 and
replacement S-key 1.0998300 native.

The original disposable account
`GDPUT7FS2DB352I4JZIQ6H7QEKR7RS5GASRAQD6GUPXRAXHK4ZKRIEW6`
remains quarantined after a pre-fix attempt with no returned hash. A later test
driver file-lock failure lost diagnostic control. Neither absent current data
nor the discovered signer defect proves that nothing was submitted. Never
retry, clear or attach a guessed transaction hash to this attempt.

## Remaining release gates

- Direct on-chain mode lacks durable uncertain-outcome recovery across browser
  restart without explicit local-retention opt-in. Do not silently enable local
  persistence or automatically retry an uncertain write.
- The refreshed Nginx base scan had no high/critical findings. The current Caddy
  image still has two high dependency findings: CVE-2026-116587 (go-jose/v3,
  no patched v3) and CVE-2026-78661 (x/net 0.60.0, fixed in 0.62.0). No waiver
  or custom cryptographic fork was accepted. See the
  [upstream go-jose advisory](https://github.com/go-jose/go-jose/security/advisories/GHSA-mgcc-cww5-fp2q).
- Encrypted browser journal crash/restart/CAS and concurrency acceptance,
  interrupted file recovery/export, supported mobile devices, full live HD
  historical evidence, and independent security/protocol review remain.
- Final immutable images, VPS DNS/TLS, backup/rollback and deployment acceptance
  remain. The API is authenticated read-only receipt inspection; it is not a
  hosted storage or transaction-submission service.

The original full-production request remains the target. A proposed restricted
custom-Testnet pilot scope has not been selected by the user.
