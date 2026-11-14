# Design option — Stellar secret-key vault chain (not yet implemented)

Status: proposal only. The current production-path IStellarLedgerRecords.ConnectAsync expects a BIP-39 mnemonic, and HD v2 uses SEP-5 derivation, an authenticated root registry, and sponsored child allocation. Do **not** silently treat a secret key as a mnemonic.

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
