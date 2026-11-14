# Independent Secret Key / Linked-Account Vault — implementation plan

Status as of 2026-11-14: **Phase 2 source implementation added (Testnet candidate), production readiness NOT verified.** HD v2 remains operational as before. The new independent S-key type, checksum-gated read-only chain inspection, signer and threshold verification, cycle/length checks, and account-provisioning cost planner are committed in Core/PWA/MAUI. Both hosts now offer explicit Recovery Phrase / Stellar Secret Key selection. The Secret Key action is labeled **Inspect linked accounts**, not Unlock or Connect, because record encryption, live writes, child provisioning, and recovery have **not** been enabled. The current production-path IStellarLedgerRecords.ConnectAsync still expects a BIP-39 mnemonic. Never interpret an S-key as a mnemonic.

## Product choice

At entry, explicitly choose either:
- **Recovery phrase (HD vault)**: BIP-39/SEP-5-derived root and deterministic children, authenticated sv:registry:v2, existing HD record/manifest implementation.
- **Stellar secret key (linked-account vault)**: import a standard Stellar S… secret, derive its public G… root, use separate versioned chain metadata and an authenticated account ownership scheme.

Both should use the shared record/folder payload model, encryption, fee quoting, operation-approval UI, sync drafts, and verified transaction receipts. They require different discovery, signing, key lifecycle and chain-integrity strategies.

## What "single secret" actually means

A ManageData pointer to the next G-address does **not** grant the root key signing authority on that account. New child accounts normally have independent random keypairs. To manage them using one root secret, assign the root public key as a signer on each child (SetOptions), configure compatible thresholds, and ensure the root signature really can authorize all intended operations. Prevent a second undisclosed key from retaining unintended authority; any master-weight changes require exact signed transaction and recovery safety checks. During initial provisioning the child private key must exist briefly to authorize its signer policy, then be zeroized where feasible. A failed or uncertain provisioning sequence must be reconciled before deleting the only child authorization secret.

A separate approach is to encrypt each child's seed under a securely derived vault data key and persist its ciphertext, but this requires more encrypted storage, key rotation/recovery handling and secret-lifecycle audit. Never put a raw child secret into ManageData.

## Stellar costs (illustrative, verify from selected Horizon before every write)

At the currently reported base reserve of 0.5 XLM:
- 1 XLM reserve per new funded account (2 base reserve units)
- 0.5 XLM for each new ManageData pointer entry
- 0.5 XLM for an additional root signer on each child

A simple linked-list child with one parent pointer and one root-signer subentry therefore requires about **2 XLM additional reserve per child** (not counting actual encrypted vault data subentries). Ten additional children would hold approximately 20 XLM extra reserve plus network fees. Account and subentry reserves are *locked minimum balances*, not fees. Reserve sponsorship can move some funding requirements to the root account, but does not eliminate the ledger's total reserve cost. If the pointers are stored differently or child signer keys are handled differently, the reserve model changes.

Non-smart-contract operations have a network minimum inclusion fee of 100 stroops (0.00001 XLM) each; network congestion can raise effective fees. Creating/funding, setting child signer policy, writing a pointer, and sponsorship setup are multiple operations and possibly multiple transactions. Do not hardcode an operation count or a fixed fee. Quote the selected network's current ledger reserve/fee values and show peak reserve, added reserve, fees spent and refund behavior separately.

## Compatibility and correctness

- Namespaces for root, child pointers and records must be versioned, domain-separated by network passphrase/root ID, and authenticated.
- Read-only chain discovery must enforce visited-set cycle detection, maximum traversal bound, valid signed ownership/authorization, expected network identity and contiguous chain invariants.
- Prepare/quote, sign, submit and independently confirm must be separate. Concurrent writers and stale tail writes must fail safely and reconcile uncertain submissions.
- On-chain account creation, signer policy and pointer publication should be made atomic where protocol permits, otherwise require a recoverable two-phase state machine with explicit ledger evidence.
- Single-root-key compromise compromises all child accounts; root key loss risks the entire vault. Do not suggest this mode is inherently safer than the HD mode.
- Testnet-first implementation: imported root validation, read-only legacy discovery, isolated signer policy, safe funding/sponsorship, atomic tail extension, encrypted records, recovery on a clean device, native/PWA host parity.
- Do not automatically migrate an existing legacy secret-key vault into HD v2; provide opt-in migration with complete on-chain/integrity verification and retained recovery until final confirmation.
- IStellarLedgerRecords.ConnectAsync(string mnemonic, ...) currently assumes an HD credential, so a typed VaultCredential + backend-strategy abstraction is needed rather than changing its behavior implicitly.
- Review docs/legacy-ledger-reference/app.js and src/StellarVault.Pwa/wwwroot/js/ledger-compat/app.js for legacy secret-key connect/encryption code; do not mistake those references for production-ready linked-chain authorization.

Implementation/safety work is substantial: core credential abstraction, secret-key signer transport, account-chain discovery and integrity, funding/sponsorship estimator, onboarding UI, local-key cleanup, error reconciliation, testnet recovery tests, and independent security review. No feature implementation, signing or network transactions are performed as part of this proposal.

## Current implementation (phase 1)

- `src/StellarVault.Core/Services/VaultCredentialMode.cs`: explicit credential discriminator and in-memory typed input, plus chain-inspection data contract. Secret values are not serialized, logged or persisted.
- `src/StellarVault.Core/Services/VaultSecretChainProvisioningPlan.cs`: pure, checked-stroops funded-child reserve and operation-fee budget. This is a planning model; not an operation, signed quote or authorization.
- `src/StellarVault.Pwa/wwwroot/js/vault-secret-chain.js`: read-only network-bound S-key inspector. It verifies Stellar StrKey checksum, Horizon network passphrase, bounded 64-account traversal, `sv:next:v1` or `sv:next` pointers (base64-encoded G-address), no duplicate conflicting pointers, root signer weights against medium/high thresholds, minimum positive thresholds, no cycles, and rejects roots with an HD-v2 registry. **This newly recognized link format is not proof of compatibility with unverified historical secret-key vaults.**
- `src/StellarVault.Pwa/Services/BrowserSecretKeyVaultDiscovery.cs`: typed JS interop adapter with post-deserialization validation. Registered in PWA and linked/registered in MAUI.
- `VaultHome.razor` and `NativeVaultWorkspace.razor`: separate credential selectors and explicitly read-only inspection. Clear S-key input after every attempt; do not alter HD login and transaction workflow.
- `tests/secret-key-chain.test.cjs`: mocked inspection tests including valid root/chain, unauthorized signer, cycles, network mismatch, checksum, HD-v2 registry rejection and zero signer thresholds.

**Not yet implemented:** production S-key record encryption format/AAD, independent on-chain manifest reader and signer transport, live funding/atomic child allocation, explicit fee approval, update/delete/cleanup, history-based recovery, hybrid mutation authorization, on-device consent/retention, native/pwa end-to-end recovery, production security review. Do not describe or present the mode as read/write capable until these are done.

### Critical recovery edge

A root public signer on a child authorizes specific child operations, but an account address link does not. All write paths must validate exact required signer weights, security thresholds, correct network, root registry mode and current tail. Child master-key policy must be resolved explicitly during atomic provisioning; a failed or uncertain write requires fresh independent network evidence before retry. Legacy linked-account formatting remains unverified and must never be silently upgraded.


## 2026-11-14 — Phase 2: independently connected encrypted record/folder backend

The Phase 1 read-only statements above describe the earlier milestone and are superseded *for the new versioned Secret Key protocol* by this section. Do not interpret Phase 2 as production maturity or full parity with the HD backend.

**New source implementation:**
- `src/StellarVault.Core/Services/IStellarSecretKeyLedger.cs` introduces a separate ledger contract: S-key unlock, read, account inventory, create/update/delete quote, chain extension quote, cancel, explicit approved submit and lock.
- `src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js` implements the distinct S-key backend. It derives a domain-separated AES-256-GCM data key with HKDF from the imported root key. Each record's AEAD additional data includes the network passphrase, root ID, record ID and random generation. It stores `skh:<16hex>` 47-byte binary authenticated commit manifests and `skc:<16hex>:<8hex generation>:<chunk index base36>` encrypted chunks in Stellar ManageData entries.
- All record chunks, manifest commitment, and old-generation cleanup are submitted in **one** transaction, limited to 90 operations. No partial multi-transaction staging is enabled. Records exceeding atomic capacity are rejected without submitting; binary file upload is not yet supported in this backend.
- Before a write, network passphrase, fees and base reserve are checked against live Horizon. Quotes show operation count, fee, peak reserve, net reserve change, target account and any child-storage funding top-up. A one-time in-memory quote expires after 120 seconds; explicit approval triggers signing. If outcome is uncertain, the quote is consumed and automatic retry is prohibited.
- Account expansion is an explicit reviewed transaction consisting of CreateAccount funded for 2 account reserve units + root signer reserve, SetOptions on the newly created child (root as signer weight 1; child master weight 0; thresholds 1), optional root-funded pointer-owner top-up, and a tail ManageData pointer `sv:next:v1`. The temporary child keypair signs setup and is cleared after approval or cancellation.
- Child record writes can include an atomic root-funded Payment for their newly consumed data-entry reserves. The chain is bounded to 64 accounts; root signer authorization and exclusive positive signer policy on children are rechecked on each read.
- Root/child records are independently recoverable from the original S-key and selected network when the complete `skh/skc` v1 record data and account pointers remain accessible. Existing `svh:/svc:` legacy-record formats and `sv:registry:v2` HD roots fail closed rather than being treated as an empty new vault.
- `src/StellarVault.Pwa/Services/BrowserSecretKeyLedger.cs` is the C# interop adapter. PWA and MAUI register the same Core contract and package the same JavaScript engine; the MAUI project links the PWA adapter source instead of cloning it.
- `src/StellarVault.UI/Components/VaultSecretKeyWorkspace.razor` is the shared modern light/dark explorer with record/folder create/edit/delete, reveal-by-choice, grid/list, nested folders, account count and explicit fee approval. PWA routes to `/secret-vault`; MAUI embeds the same component under its native shell.
- Existing `IStellarLedgerRecords.ConnectAsync(mnemonic,...)` and HD registry/derivation remain unchanged. Native background loss and PWA pagehide revoke S-key signing sessions.
- `tests/secret-key-ledger.test.cjs` provides isolated SDK/Horizon mocks for encrypted record write/read/update/delete, signer-authorized child provisioning and funding, one-shot quote lifecycle, mainnet mutation blocking, legacy collision, clean-session recovery and corruption rejection.

**Validation evidence:** JS syntax parsed; Razor tag structure and JS/DI asset references inspected; an in-memory mocked JS ledger run completed open, encrypted create, edit, delete, chain expansion and child record write including a reserve top-up, with five simulated transaction receipts. The repository Node test files were committed but not executed in their normal Node test runner. No actual `dotnet build`, Android/iOS WebView run, independent historical Horizon receipt verification, clean-device Testnet trial or real transaction submission has been performed.

**Critical outstanding functionality and production blockers:**
1. **Files:** binary file upload/export and multi-transaction verified chunking do not exist in this Secret Key backend.
2. **Hybrid/offline:** encrypted local drafts, account-scoped mutation approval/reconciliation and offline folder/record synchronization must be wired to the S-key strategy; HD-only Sync must never sign S-key drafts.
3. **Historical compatibility:** the original pre-HD secret-key link and ciphertext formats have not been recovered and verified. There is no silent legacy migration.
4. **Concurrency:** before Mainnet, implement reliable optimistic-concurrency/transaction-history conflict strategy (Stellar ManageData has no native compare-and-swap). Current snapshot rechecks alone are insufficient against simultaneous writers.
5. **Independent historical proof:** current-state recovery after submission is not proof of complete historical inclusion. Add independent receipt, operation-source, ciphertext and decrypted-record field verification before production.
6. **Key lifecycle and root registry:** verify signer policy, chain consistency and key cleanup on real platforms, plus authenticated root registry and durable recovery of failed/uncertain child provision attempts.
7. **Device/browser:** run .NET 10 builds, Node tests, Android/iOS device checks, real Testnet funding and recovery on a clean device, and external cryptographic/security review.

Public Mainnet **mutations remain disabled in JavaScript**; read-only inspection/unlock may still work. Never describe this as full cross-mode production parity until every gap is closed.
