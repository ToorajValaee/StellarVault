# First real private-network ledger write/read test

This verifies the **on-chain HD-v2 record workflow**, not the separate browser-only text editor.

## Prerequisites
- A funded SEP-5 index-0 root account on your private Stellar network, derived from a disposable BIP-39 mnemonic.
- Private Horizon reachable by your browser (HTTPS, or localhost development HTTP) with CORS permitted.
- Enough spendable XLM/reserve for sponsored child account creation and ManageData entries. Transactions cost network fees.
- The root account controls its transaction signing keys under the network's current signer policy. The app signs locally.

## Test
1. Pull `feature/maui-shared-app` and run `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release`.
2. Launch the PWA. Choose **Custom**, enter private Horizon root URL, click **Detect Network**, and verify the detected passphrase matches the intended network.
3. Enter disposable mnemonic and local encryption passphrase, then click **ACCESS VAULT**. The local vault opens independently of Horizon.
4. Under **STELLAR LEDGER**, confirm **Signed ledger session connected**. If it fails, record the exact message. This ledger session requires a funded root; local IndexedDB success alone does not prove it.
5. If shown, click **INITIALIZE ACCOUNT REGISTRY** once. It creates the original HD-v2 authenticated chain registry on-chain. Funding/reserve requirements apply.
6. Enter a test title and test content, then click **PUBLISH TO STELLAR**. A confirmed transaction hash is required for success.
7. Click **READ FROM STELLAR** and verify the decrypted record contents. This path queries Horizon and reads the original `svh:` / `svc:` data entries, not IndexedDB.
8. Lock and reopen, then re-read the ledger record.
9. Repeat from a fresh browser profile or device with empty IndexedDB: same mnemonic and network (and any local passphrase needed for the separate local-vault namespace), reconnect, read the same on-chain record.

## Important boundaries
- The new **STELLAR LEDGER** form is the on-chain writer. **LOCAL-ONLY RECORDS** below it still use IndexedDB; they are not automatically published.
- Original HD-v2 ManageData manifests have a 64-byte chunk limit and account reserve/fee costs. Large record/file publishing, update, delete, and complete Core storage-adapter unification are not yet migrated.
- Do not publish real secrets until a successful Release build, real-network verification and independent cryptographic review.
- If a transaction hash was returned but the follow-up refresh failed, **do not click publish again** before checking Horizon: the mutation may already have committed.
- The app does not fund the root automatically.
