# StellarVault controlled Testnet pilot — release restrictions

Status: **Not a production release certificate.** This checklist is for evaluating an experimental build with disposable accounts and fabricated data. Even restricted access cannot guarantee preservation of funds or data.

## Allowed pilot scope

- Use **Stellar Testnet** with fresh, separately funded, disposable test accounts. No Mainnet S-key mutations.
- Use only **synthetic example passwords and files**; no real passwords, seed phrases that control valuable assets, private customer information, or unique/unbacked-up files.
- Label the site **Experimental / Testnet only** prominently in access and release communications. Do not advertise it as audited, production-secure, or suitable for storing valuable credentials.
- Limit access to an invited test group; serve over HTTPS; publish a contact for reporting possible security/data-loss incidents.
- Document that locally encrypted drafts are **device-specific** and may be lost if the browser profile, app storage, or filesystem is cleared; neither a transaction hash nor a current record snapshot alone guarantees historic publication proof.
- Keep the exact deployed build, its commit SHA, and tested browser/device versions in the pilot log.

## Mandatory pre-invitation verification

- [ ] Run the Node regression suite, .NET 10 Core/UI/PWA Release builds, and Core cryptography/linked queue smoke suite from `docs/verification/manual-validation.md` on the exact commit being deployed.
- [ ] Publish and serve the PWA on a controlled HTTPS origin; inspect console/network errors and PWA refresh/update behavior.
- [ ] Test HD and imported S-key vaults separately, including correct and incorrect credentials, lock/reopen, network selection, consent toggle, and cross-mode isolation.
- [ ] Demonstrate local-only encrypted draft create/revise/reopen without accidental on-chain transactions; test storage denied/full and clearing device data only on disposable fixtures.
- [ ] Confirm every Testnet write presents a fresh transaction-specific fee/reserve quote and explicit approval; no automatic retry occurs after unknown results.
- [ ] Simulate uncertain record submission and linked account expansion. Confirm durable `Submitting/Uncertain` persists across restart, blocks all further queued publication, and cannot be cleared merely by a successful current-state read or receipt.
- [ ] Validate an independent clean-device recovery of Testnet on-chain data. Do not assume locally-only drafts survive clean-device recovery.
- [ ] Record and review the known limitations: linked historical operation/signer proof, queued update/delete, linked file publication, durable reconciliation, cross-host validation, and security audit are incomplete.
- [ ] Agree on pilot stop conditions, rollback owner, and incident triage before admitting testers.

## Stop conditions — immediately suspend new writes

- Any accepted or uncertain transaction is followed by a duplicate write, unexpected signer change, or surprising account-chain growth.
- A reopened vault fails to decrypt data previously verified recoverable from the same intended credential/network.
- A queued mutation becomes `Confirmed` without independent historic transaction-operation/record proof.
- Encrypted local records appear under a different user, vault root, network, or credential architecture.
- Any new key exposure, plaintext persistence, or critical dependency vulnerability is discovered.

Do not retry or clear an uncertain journal just to make the pilot proceed. Preserve encrypted journal artifacts for controlled investigation; never collect seed phrases, secret keys or plaintext passwords from testers.

## Production gate — outside the pilot

Before handling real secrets or funds, finish historical operation and signer verification with durable reconciliation, linked file and queue parity, cross-device recovery, security review and remediation, and the manual checks in `PWA-RELEASE-GATE.md`. Pilot success does not satisfy these release requirements.

## Additional signed-transaction receipt checks (2026-11-15)
- [ ] On an interrupted S-key submission, confirm the encrypted local queue retains the **signed transaction hash** when signing succeeded; that hash is public and cannot authorize retry.
- [ ] Restart the browser/app and reconnect the same independent S-key signer. Read-only recovery should look up the durable hash using the selected HTTPS Horizon network even if volatile runtime diagnostics are gone.
- [ ] Confirm that an observed receipt, a matching live record or a new linked tail **still cannot mark the queue Confirmed** without full historical record-operation/signature verification.
- [ ] Verify manually that a failed pre-broadcast signing/hash computation does not submit any transaction. If persistence fails after signing, retain quarantine and never retry blindly.

## Durable checkpoint verification (journaled linked transactions)
- [ ] Inspect an approved linked encrypted draft or expansion: confirm the 64-hex fully signed transaction hash appears in durable `Submitting` before Horizon receives the transaction.
- [ ] Simulate encrypted queue CAS failure at the hash checkpoint. Verify the signer **never** submits the transaction, and the queue is not silently returned to Pending.
- [ ] Terminate the browser/app after signing/checkpointing but before the network outcome is known; reconnect to the same account and verify the public hash remains in the encrypted journal for read-only receipt investigation.
- [ ] Confirm returned Horizon hash must equal the pinned signed hash; any disagreement blocks the queue without retries or auto-confirmation.

## Read-only historical S-key record verification (2026-11-15)
- [ ] Confirm a successful real Testnet S-key record creation produces a Horizon receipt and matching `skh/skc` operation history for the exact signed transaction hash.
- [ ] Interrupt and reopen the encrypted local queue. Confirm that receipt, matching current record and historical encrypted chunk commitment remain separately labeled **advisory**; no retry or `Confirmed` transition occurs.
- [ ] Run the new mocked `LinkedRecordHistorySmoke` suite to reject wrong generation, missing, duplicate or reordered chunks, cross-account writes and ciphertext digest mismatches.
- [ ] Do not open the pilot with real secrets: historical signer authorization, decryption of historical ciphertext to the approved draft and durable quarantine release remain incomplete.

## Historical expansion smoke
- [ ] Run `LinkedExpansionHistorySmoke` via the .NET Core tests on the exact deployed feature commit.
- [ ] On disposable Testnet S-key expansion, inspect the successful Horizon transaction for exact child CreateAccount, signer SetOptions, and parent link, including native reserve top-up when the linked tail is not the root.
- [ ] Reject wrong signer weights, master weight, thresholds, duplicate parent pointer, child destination mismatch, and wrong transaction ID. A read-only match must not unblock the journal.
