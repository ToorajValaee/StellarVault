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
