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


## Pre-launch file format redesign — unlimited logical file lengths

**Product decision:** no deployed-user file-format backward compatibility is required. The current flat SVFILE1/SVFILE2 and direct/linked 1 MiB/96 KiB paths must not be rebranded as unlimited. Do not silently increase legacy constants. New files should converge on one streamed, versioned tree format before launch; remove old writers and migration code only when every host and recovery path has moved to the replacement.

**Foundation implemented:** `VaultStreamingFileTree` processes a `Stream` in small chunks, incrementally hashes the entire content, emits bounded-fanout child/branch nodes and a final file root. Its reader verifies each node digest and the complete file hash while writing to a temporary destination; that destination must be destroyed on error and promoted only after successful verification. The plaintext callback is intentionally transport-agnostic, and **does not perform encryption, staging, signing, transmission, or transaction reconciliation**. The Core smoke exercises content above the previous 1 MiB cap, corruption rejection and sink failure.

**Integration remaining (REQUIRED before claiming arbitrary-size uploads):**

1. Adopt one encrypted node store/journal contract for HD, linked S-key, PWA and MAUI. Each emitted node must be encrypted, durably staged and identified before any transaction; no whole-file bytes or flat file part array may enter the queue.
2. Connect file-root/child nodes to the existing per-transaction exact fee quote, user approval, signed-hash checkpoint, verified historical receipt and CAS reconciliation. Manifest/root publishes last after all descendants are confirmed. Do not add a background retry or a parallel network signing engine.
3. Persist a bounded resumable cursor and stable node identifiers across crashes. Refuse publication if a prior node is Submitting/Uncertain/Conflict or if any ancestor/descendant is missing; do not infer an absent transaction from a Horizon timeout.
4. Expose streamed upload/download in PWA and MAUI without `IBrowserFile.OpenReadStream` whole-file buffering or `byte[]` download aggregation. Use temporary encrypted/native-file sinks; verify before presenting the completed file.
5. Test multi-GiB synthetic file streams, interrupted writes at every node and root boundary, device quota exhaustion, namespace isolation, cross-device restore, and fee/reserve affordability. The Stellar ledger and device have **physical and economic limits**: 'unlimited' means *no arbitrary application-wide byte cap*, not infinite network capacity or affordable unlimited storage.
6. Update the existing `VaultChunkObjectStore` local chunk manifest when converging protocols: it currently caps at 4,096 × 256 KiB chunks and stores total length as a 32-bit integer. Removing UI caps alone does not remove this underlying finite limit.

Until the integration tests pass, the old on-chain file size caps remain enforced by the existing shipping UI. Do not expose a new unlimited-size claim.


## SVTREE3 implementation review (2026-11-15)

The new **S-key** linked file path now stages a hierarchical streamed tree through the encrypted per-node queue with individual fees, signed hash checkpoints, history proof and manifest-last reconciliation. Published roots can be reconstructed and streamed through PWA/MAUI file savers. The local encrypted PWA file view also uses two-step verification and bounded streaming export. This is **source implementation, not live assurance**: tests on the new feature commits, real browser user-gesture behavior, native abortability, multi-account capacity expansion and actual Testnet signed-history verification are outstanding.

**Still NO-GO:** HD/mnemonic on-chain file UI and hybrid file journal have not been migrated to SVTREE3, and the legacy 1 MiB cap remains there. Deleting a fully published file tree remains disabled rather than risking orphaned descendants. Native/browser file-saver interruption, root-pointer recovery after device loss, account-chain maximum depth, transaction capacity and fees can still limit practical file size. No claim of literally infinite file storage is authorized.
