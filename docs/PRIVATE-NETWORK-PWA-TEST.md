# Private Stellar Network — PWA Test Procedure

Use disposable mnemonic words and test data. The current PWA is not yet a production release.

1. Update to `feature/maui-shared-app`, restore, and build `src/StellarVault.Pwa/StellarVault.Pwa.csproj`.
2. Launch the PWA, choose **Custom**, then provide the exact **Horizon root URL** for your private network (for example `https://horizon.example.org/`).
3. Choose **Detect Network**. The PWA requests the root endpoint's `network_passphrase`. It is displayed for confirmation, and its SHA-256 digest namespaces the browser's encrypted vault credentials. Do not change the Horizon URL after detection without detecting again.
4. Provide a *disposable* BIP-39 mnemonic and encryption passphrase and choose **Access Vault**.
5. Save test text, lock, reopen with the same network and credentials, and read the text. Test an incorrect encryption passphrase.
6. Try another private network with a **different network passphrase** using the same mnemonic; it should not open the prior network's vault.
7. If your index-0 account is funded, the browser can run read-only Horizon discovery for child accounts. Missing accounts do not prevent opening local encrypted records.

## Hosting requirements

- **HTTPS Horizon URL** accessible from the browser. For local development, plain HTTP is accepted only for `localhost`, `127.0.0.1`, or `[::1]` on the same device running the browser.
- Horizon must return `network_passphrase` on its root endpoint, respond to `/accounts/{G-address}`, and permit cross-origin browser GET requests through appropriate CORS headers.
- The selected custom passphrase is the network identity. Changing it intentionally opens a different vault namespace; **do not change the private network's passphrase after storing data**.
- This is local-only IndexedDB persistence. There is no cross-device backup or publishing to the ledger yet. Keep tests disposable.
- No public-key signing or wallet integration is required for local storage. The UI does **not** prove Stellar account ownership.

## Build

```powershell
git pull origin feature/maui-shared-app
dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release
dotnet run --project src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release
```
