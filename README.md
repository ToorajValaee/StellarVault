# Stellar Vault PWA

A mobile-first Blazor WebAssembly PWA that stores individually encrypted credential records in classic Stellar `ManageData` entries. When the active account node reaches its configured capacity, the root database keypair creates a child account, makes the root public key its signer, disables the child master key, and links it through `sv:next`.

The interface is styled like a CRT-era console with scanlines, pixel textures, sharp borders, green/amber phosphor themes, and touch-friendly mobile controls.

Stellar Vault Terminal is powered by **BADJE.IR**. Project contact: **Tooraj Valaee** — **tooraj.valaee@live.com**.

## Features

- Blazor WebAssembly PWA for mobile and desktop
- iPhone/iPad installation instructions and Chromium native install prompt
- iOS standalone viewport stabilization, non-translucent status bar, and safe-area support
- 16px mobile form controls to prevent Safari focus zoom
- Testnet, Public, and custom/private Stellar networks
- Automatic custom-network discovery from Horizon:
  - network passphrase
  - latest base reserve
  - current/recommended fee per operation
  - Horizon version
- Manual fallback when Horizon discovery is blocked or unsupported
- Network settings saved locally; root secret is never persisted by the app
- Searchable credential list with reveal and clipboard actions
- Add, edit, and delete flows
- Dedicated popup approval for add, edit, and delete cost estimates
- Separate display of:
  - transaction fees that are spent
  - reserve temporarily locked by ledger entries
  - reserve released by deletion
  - child-account funding transfers
- AES-256-GCM encryption and HKDF-SHA256 derivation through Web Crypto
- Automatic 64-byte `ManageData` chunking
- Versioned record generations and manifest-last commits
- Automatic linked child-account creation
- Root key controls every child node
- Contact and private-network account request page
- Offline-cached application shell

## Run

Requirements:

- .NET 8 SDK
- An existing funded Stellar root account
- A Horizon endpoint reachable by the browser

```powershell
dotnet restore
dotnet run
```

Open the HTTPS address shown by `dotnet run`. When using a plain-HTTP private Horizon server during development, open the app from its HTTP development address; browsers block an HTTPS page from calling HTTP resources.

## Custom-network discovery

Enter the Horizon URL and press **DETECT PASSPHRASE + FEES**, or leave the field after typing the URL. The browser reads:

```text
GET /
GET /ledgers?order=desc&limit=1
GET /fee_stats
```

The root Horizon response supplies the network passphrase. The latest ledger supplies the base reserve, and fee statistics supply a suggested per-operation fee bid.

Horizon must allow the PWA origin through CORS. If discovery fails, the form remains editable so the values can be entered manually.

## Child funding and node limit

The old prototype used a fixed child starting balance of `50` native units. This version calculates it from the actual network base reserve and the selected node capacity:

```text
recommended child funding = (node data-entry limit + 4) × base reserve
```

The four reserve units represent:

```text
2  account baseline
1  root signer on the child
1  safety reserve
```

The selected data-entry limit reserves capacity for the node's `ManageData` entries. The amount is a transfer from the root account to another account controlled by the same root signer. It is not burned as a fee.

The node limit is kept under **Advanced Ledger Parameters** because it is a storage/capital tradeoff:

- Lower limit: less balance locked in each node, but more child accounts are created.
- Higher limit: fewer child accounts, but more native balance is transferred into each child as future capacity. Only the ledger-required portion is locked as entries are created.

The default is `80` data entries per node. With a base reserve of `0.5`, the calculated child funding is `42` native units.

## Cost estimates

Before a mutation is signed, the app calculates and displays:

- operation count
- transaction count
- encrypted chunk count
- data entries added and removed
- estimated network fee
- peak additional reserve requirement
- net reserve change after cleanup
- child-account funding transfer, when a new node is required

Submitting an add or edit form opens a dedicated approval popup. The popup keeps the estimated fee, reserve impact, operation count, and approval controls visible without requiring the user to scroll back through the form. Delete uses the same approval flow. No transaction is signed until the popup approval button is pressed.

Network fees are spent. Reserve is balance that must remain on an account while its account structure and data entries exist. Removing data entries can release reserve, but it does not refund transaction fees.

## PWA installation

### Android and desktop Chromium

The app captures `beforeinstallprompt` and displays its own retro install banner. Pressing **INSTALL** opens the browser's native installation dialog when available.

### iPhone and iPad

iOS does not expose the Chromium installation prompt to the page. The app therefore shows manual instructions:

1. Open the site in Safari.
2. Tap **Share**.
3. Choose **Add to Home Screen**.
4. Tap **Add**.

After installation, the app uses a non-translucent iOS status bar so the operating system reserves the top status area instead of requiring the header to add that inset again. Left, right, and bottom safe areas still use `env(safe-area-inset-*)`. A small viewport synchronizer refreshes the first standalone layout without waiting for the user to scroll.

Mobile inputs use a computed font size of at least 16px. This prevents Safari from automatically zooming the page when a form field receives focus while preserving normal pinch zoom for accessibility.

Use HTTPS in production. `localhost` is accepted for local development, but a plain HTTP LAN address usually isn't an installable secure context.

## Contact and private-network accounts

The public home page and `/contact` include:

```text
Tooraj Valaee
tooraj.valaee@live.com
Powered by BADJE.IR
```

Users who do not have an account on the BADJE private Stellar network can use the prefilled email action to request account provisioning.

## Ledger format

```text
sv:next                                  -> raw 32-byte public key of the next account
svh:<16-char-record-id>                  -> committed manifest
svc:<record-id>:<generation>:0000        -> encrypted data chunk
svc:<record-id>:<generation>:0001        -> encrypted data chunk
...
```

Manifest version 2 is 51 bytes and contains a four-byte generation identifier. Legacy version-1 records remain readable. During an edit, the new encrypted chunks are staged first, the new manifest is committed last, and old entries are cleaned afterward.

Delete removes matching manifests first so the record immediately disappears from normal reads, then removes the encrypted chunks. Historical transaction data may remain accessible on a Stellar history service even after current ledger entries are deleted.

## Security notes

- This is an experimental prototype and has not been professionally audited.
- All ciphertext is publicly retrievable when used on a public Stellar network.
- Anyone who obtains the root secret can sign database operations and derive the vault encryption key.
- The app does not save the root secret to local storage, IndexedDB, cookies, or application settings.
- The secret and decrypted records exist in browser memory while the vault is open.
- Clipboard history, browser extensions, screenshots, and a compromised device can expose revealed values.
- Use HTTPS for production.
- Back up the root secret securely. Losing it makes the database inaccessible.

## Publish

```powershell
dotnet publish -c Release
```

Deploy:

```text
bin/Release/net8.0/publish/wwwroot
```

The web server must fall back to `index.html` for client routes such as `/vault`, `/add`, `/edit/<record-id>`, `/system`, and `/contact`.

Example Nginx configuration:

```nginx
location / {
    try_files $uri $uri/ /index.html;
}
```

## Horizon CORS example

```nginx
add_header Access-Control-Allow-Origin "https://vault.example.com" always;
add_header Access-Control-Allow-Methods "GET, POST, OPTIONS" always;
add_header Access-Control-Allow-Headers "Content-Type, Authorization" always;

if ($request_method = OPTIONS) {
    return 204;
}
```

Restrict the allowed origin to the deployed app domain.

## Docker

```powershell
docker build -t stellar-vault-pwa .
docker run --rm -p 8080:80 stellar-vault-pwa
```

Open `http://localhost:8080`.

## Updating an installed copy

An existing service worker may keep an older UI briefly. After deployment:

1. Close installed app windows.
2. Reload the browser version.
3. If the old bundle remains, clear site data or unregister the old service worker once.
4. Reload again.
