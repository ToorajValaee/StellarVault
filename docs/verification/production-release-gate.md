# StellarVault production release gate

**Status (2026-11-15): BLOCKED.** Implementation present in source is not release evidence. Do not enable Secret Key Mainnet writes, deploy with real seed phrases, or claim that a green compiler/static test suite constitutes secure storage.

## Scope and automated verification

Release candidates must identify an immutable commit SHA, exact dependencies and target devices. Do not test one commit and ship another.

From the repository root on a machine with Node.js and .NET 10:

```powershell
pwsh -File scripts/verify-release.ps1
# On a machine with an installed MAUI Android workload:
pwsh -File scripts/verify-release.ps1 -Android
```

The script performs JS signer syntax validation, ALL Node `*.test.cjs` regressions, Release builds of Core/UI/PWA, and the native Core cryptography/linked smoke suite. It does not send transactions or run cloud CI. Record exact stdout/stderr, commit, SDK version, device and OS version in a protected internal release dossier; never record secrets.

## Manual evidence: every item mandatory

| Gate | Acceptance evidence required | State |
| --- | --- | --- |
| Core/UI/PWA reproducibility | Local Release script green on exact commit, no waived failures | **Unverified on latest commit** |
| Native hosts | Windows and Android real-device install/uninstall/reopen, secure storage and picker/download parity | **Unverified** |
| HD mnemonic path | Fresh funded disposable Testnet account, publish, independent clean-device decrypt and full folder/file reconstruction | **Unverified** |
| Imported Secret Key path | Separate disposable Testnet root, quoted create/update/delete, child expansion, clean-device recovery, no HD fallback | **Unverified** |
| Historical transaction proof | Actual signed XDR, signatures, network hash, expected operations, manifest/chunk ciphertext, original approved plaintext, negative tampering fixtures | **Unverified on live network** |
| Crash-safe journal | Terminate after durable signed-hash checkpoint and at submission ambiguity; restart, read-only verify and CAS confirm exactly once; never replay unknown attempts | **Unverified on live devices** |
| Concurrency | Two devices and multiple browser tabs attempt conflicting updates, same tail expansion, wrong account/network; fail closed without duplicate or cross-vault state | **Unverified** |
| File integrity | HD 1 MiB protocol and linked SVFILE1/SVFILE2 supported sizes, interrupted multipart manifest-last writes, verified SHA-256 downloads; no partial file represented complete | **Unverified on live network** |
| Storage and lifecycle | Explicit consent, locked/backgrounded session, corrupted/evicted IndexedDB and native files, CSP/HTTPS/PWA upgrade, forgotten vault and cleanup, backup/recovery guide | **Unverified** |
| Security review | Independent crypto/protocol/app assessment, dependency/SBOM review, remediation with evidence, threat model approved | **Not completed** |
| Operational readiness | Versioned migrations, rollback and incident runbooks, monitoring excluding secrets, signed artifact provenance | **Not completed** |

Do not bypass unimplemented capability gates to satisfy these checks. For linked S-key, multipart file deletion and files above the versioned 96 KiB maximum remain unsupported; direct non-journaled S-key crash ambiguity and legacy pre-HD migration are still outstanding. Record a deliberate release-scoped exclusion or implement and test these capabilities before any public promise of support.

## Manual fault-injection matrix

For HD and imported S-key **separately**, using synthetic Testnet data only:

1. Kill process/browser **before signing**, **after signing before submit**, **after submit before Horizon response**, **after receipt before encrypted CAS**, and **during CAS**.
2. For each boundary reopen with correct and wrong credentials/network, confirm journal remains scoped and never auto-retries; only independently verified history allows a durable confirmed transition.
3. Change a parent record or linked tail from another session between quote and submit; require fresh preflight and rejection without a second transaction.
4. For multipart files, fail after each part and before manifest. Ensure incomplete parts cannot be downloaded as a complete file, and a missing part never permits final manifest publication.
5. Simulate offline storage quota exhaustion, rejected IndexedDB transaction, corrupted encrypted payload, deleted local store and device profile eviction. Ensure no unexpected success messages or plaintext fallback.
6. Verify historical proof rejects wrong signer, wrong network, modified ciphertext, wrong record ID, stale generation, missing/duplicate/reordered operations, wrong transaction hash and deleted/replaced manifest.

Record transaction hashes (public), result classification and redacted log references; **never collect recovery phrases, imported secret keys, vault passphrases or test plaintext from participants**.

## Decision rule

**GO** only when every applicable gate has dated evidence reviewed by a designated security/release owner, no open critical/high security findings remain, recoverability is demonstrated on separate clean devices, and the final artifact matches the tested commit. **NO-GO** for any unresolved ambiguous submission, security gap, missing independent proof or unsupported promised capability. Any later source change invalidates the prior green automated build record.

This document is a release gate, **not** a statement that verification occurred. Controlled pilot instructions: [controlled-testnet-pilot.md](controlled-testnet-pilot.md).
