# PWA local encrypted vault mode (pre-release)

The browser PWA now supports **local-only** Create / Unlock using a public Stellar G-address as a *namespace* and a strong passphrase to encrypt a randomly generated vault data key.

This is **not Stellar wallet authentication**: anyone can type an account address and create a separate local encrypted vault on their own device. No ability to transact, sign, prove account ownership, or synchronize to Stellar is implied. SWallet integration is a future, separate proof provider. MAUI retains fail-closed authorization until its own platform policy is reviewed.

## Manual smoke test (disposable account and test data only)

1. Run `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Debug` then `dotnet run --project src/StellarVault.Pwa/StellarVault.Pwa.csproj`.
2. Open a desktop browser and enter a valid Stellar G-address (it may be unfunded for the local-only test). Click **Check local vault**.
3. If no credentials exist, select **Create new vault** and enter a strong *test* passphrase.
4. Save a test text record. Close and reopen the browser (same origin), enter the same account, check local vault, and unlock with the same passphrase.
5. Read the record, edit with version checks, verify its changes persist, then remove it.
6. Try an incorrect passphrase and confirm the vault remains locked. Try a different public address and confirm it has a separate namespace.
7. Upload/download a test file and verify its bytes round trip; test file removal and stale versions.
8. Use browser devtools to ensure IndexedDB contains only encrypted data/metadata, and check application/console failures.

No access to real keys or funds should be entrusted to this pre-release mode. Browser IndexedDB is vulnerable to eviction, profile deletion, and unavailable cross-device syncing; encrypted export/recovery and independent security verification remain release blockers.
