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

- [ ] Test both the three-operation initial and four-operation existing-tail expansion paths against real Testnet history; confirm signer weights, root funding, and parent StrKey checksums.

## Revision-pinned linked update/delete acceptance (2026-11-15)
- [ ] With a disposable Testnet S-key and consented local queue, stage an update to an existing encrypted record. Confirm there is no network transaction before separate fee approval, and that its original `skh` manifest revision is retained after reopening the queue.
- [ ] Modify that record through another signer/session after staging, then verify fee preparation rejects the stale original manifest without broadcasting.
- [ ] Modify the record after a fee quote but before approval; verify pre-broadcast recheck rejects the stale quote and leaves the draft Pending.
- [ ] Stage and explicitly approve deletion of a published record with no children; confirm its current absence and that the durable local journal remains `Uncertain`, NOT `Confirmed`, even after app restart.
- [ ] Attempt deleting a folder with published or locally pending children and moving a folder inside its own descendant; both must fail without submission.
- [ ] Run updated Node and Core smoke regressions and PWA/MAUI Release builds before using these UI flows. Mainnet/real credentials remain excluded.

- [ ] Stage a remote update/deletion while a second session changes the record between manifest reads. Verify staging refuses to attach a new revision to old content.
- [ ] Verify malformed/noncanonical base64 or non-v1 original manifest versions are rejected by both PWA and MAUI encrypted queue adapters.
- [ ] Verify pending deletion payloads cannot be modified by CAS; only discarding the never-submitted deletion intent is allowed.

## Proof-bound linked reconciliation and small-file preview (2026-11-15)
- [ ] Run Node source/behavior suites, native Core smoke and Release PWA build at the exact feature HEAD; also build `net10.0-android` on a configured Android SDK machine.
- [ ] With disposable Testnet S-key A, stage a create, approve its exact fee once, confirm it becomes `Uncertain`. Restart the app, reconnect S-key A and its local encrypted queue. Verify the read-only receipt/operation view and invoke **Verify signed history and finalize journal**; require a single immutable `Confirmed` CAS with no extra transaction.
- [ ] Repeat for update and delete using the pinned original `skh` manifest. Alter the original record from another signer after staging and again after quoting: both stale writes must fail without network broadcast.
- [ ] Repeat for first linked-account expansion and another expansion from an existing child tail. Require root/child Ed25519 signatures, correct signer thresholds, selected parent pointer, native reserve top-up when required, and a confirmed journal before the next queued item becomes eligible.
- [ ] Interrupt immediately after pre-broadcast signed-hash CAS but before Horizon responds, and again before the following `Uncertain` CAS. The durable `Submitting` and `Uncertain` versions must both be safely finalizable when the exact historical hash exists.
- [ ] Negative: wrong transaction hash, wrong XDR signature, wrong original manifest, missing chunk, malformed/stale tags or unexpected operation, missing child signature, different Horizon passphrase and changed account/network/epoch must NEVER confirm or permit retry.
- [ ] Upload a synthetic 2 KiB file through linked local encrypted staging. Confirm it remains local before fee approval; after the signed transaction, verify and finalize the journal, then download and compare the bytes and SHA-256 with the original. Confirm 0-byte, 2049-byte, bad/missing checksum and multipart attempts are rejected.
- [ ] Verify browser lock, offline-only unlock, consent revocation, page reload and Android backgrounding clear volatile keys and file-selection buffers without silently changing durable journal state.
- [ ] Do not use mainnet, actual secrets or valuable funds until multipart file recovery, legacy migration as required, real-device Testnet interruption testing and independent security review are completed.

## SVFILE1 deletion lifecycle regression
- [ ] After publishing and historically confirming a disposable Testnet SVFILE1 file, stage its deletion. Verify the encrypted queue labels it as a file and does not report corruption.
- [ ] Confirm the original file deletion snapshot cannot be opened for editing, and deleting does not submit a transaction until a *new* explicit fee approval.
- [ ] Verify remote deletion, persistent `Uncertain` journal and explicit signed-history reconciliation to `Confirmed`; the transaction count must increase only once.

- [ ] Attempt to stage deletion of a Testnet linked legacy unchecksummed or malformed SVFILE1 and an unsupported SVFILE2 manifest. Both must be refused *before* creating a local queue entry; neither may send a Stellar transaction.
- [ ] Confirm a valid checksummed SVFILE1 delete remains typed and reviewable, and its historical proof and final queue state survive a browser/MAUI restart.

## Access, resize, private network and local file-size UI regression
- [ ] On desktop PWA, open **New record**, **Edit record**, and record preview; drag the right pane border in both directions and use keyboard left/right arrows on the handle. Verify left content resizes without clipping, including at the 1100px breakpoint.
- [ ] With a short record/folder tree, confirm the entire left explorer background fills the viewport-height records panel; with a long tree, confirm both sides remain usable.
- [ ] On access screen, select Stellar-only and local encrypted storage repeatedly; the choices must remain visible, the radio text stays vertically aligned and the green left welcome copy must not shift up/down.
- [ ] Choose local encrypted storage and device unlock in HD mode, then S-key mode. Verify HD passkey enrollment still requires the HD passphrase and S-key enrollment only follows verification of its independent local queue passphrase. Confirm S-key and HD passkeys never share namespaces; fallback to passphrase when WebAuthn PRF is unsupported.
- [ ] On a custom/private Stellar network, fee/reserve descriptions say native asset rather than assuming XLM. SDK's native asset checks must remain correct.
- [ ] Upload/download a **locally encrypted** synthetic file larger than 1 MiB; verify integrity and no UI size rejection (subject to real device quota and memory). A **direct Stellar** file up to the existing protocol 1 MiB ceiling must use separate approved multipart checkpointed transactions. Larger on-chain and S-key multipart files remain unsupported and must show their actual protocol limits.

## Bounded linked SVFILE2 multipart Testnet acceptance
- [ ] Run current `node --test tests/secret-key-chain.test.cjs tests/secret-key-ledger.test.cjs tests/pwa-release-smoke.test.cjs`, the native Core smoke and Release builds after pulling new commits.
- [ ] Stage a disposable synthetic 4096-byte file in the independent S-key queue. Verify there are exactly two part drafts and one final manifest draft, and absolutely no network transaction during staging.
- [ ] Try approving the second part and the manifest before the first part has been signed and historically confirmed. Both must be blocked. Approve part 1's exact fee; verify `Uncertain` after submission and `Confirmed` only after historical XDR/decrypted-record proof; repeat for part 2.
- [ ] Try deleting or modifying the on-chain first part from a second session before the second quote or final manifest; the fresh remote mismatch must prevent further publication.
- [ ] Approve the manifest only after all preceding parts are independently confirmed. Download via SVFILE2, verify exact original SHA-256 and no duplicated transactions.
- [ ] Attempt 98305-byte and larger S-key files and multipart manifest deletion; both must remain unsupported. Verify the general local encrypted file store is not affected by the S-key on-chain limit.
- [ ] Interrupt local multipart staging and after submitting each part. Verify partial *pending* staging is either rolled back or explicitly requires local cleanup, while all `Submitting/Uncertain` entries remain quarantined until individual signed-history confirmation.
