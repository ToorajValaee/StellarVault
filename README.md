# Stellar Vault PWA

A mobile-first Blazor WebAssembly PWA that stores individually encrypted credential records in classic Stellar `ManageData` entries. When a node reaches its configured capacity, the root database keypair creates a child account, makes the root public key its signer, disables the child master key, and links it through `sv:next`.

The UI is intentionally styled like a CRT-era console with scanlines, pixel textures, sharp borders, green/amber phosphor themes, and touch-friendly mobile controls.

## Features

- Blazor WebAssembly PWA; installable on mobile and desktop
- Testnet, Public, and custom/private Stellar network selection
- Custom Horizon URL and exact network passphrase
- Network settings saved locally; root secret is not persisted
- Searchable credential list
- Record detail view with reveal and clipboard actions
- Add credential form with local password generator
- AES-256-GCM encryption and HKDF-SHA256 key derivation through the browser Web Crypto API
- Automatic 64-byte `ManageData` chunking
- Manifest-last commit pattern and SHA-256 integrity verification
- Automatic linked child-account creation
- Root key controls all child nodes
- Account-chain diagnostics page
- Green and amber phosphor themes
- Offline-cached application shell

## Requirements

- .NET 8 SDK
- An existing, funded Stellar root account on the selected network
- Enough native balance for transaction fees and child-account reserves
- A Horizon endpoint reachable from the browser

## Run

```powershell
dotnet restore
dotnet run
```

Open the HTTPS address shown by `dotnet run`. For a local private Horizon endpoint using plain HTTP, open the app through its HTTP development address instead; browsers block HTTPS pages from calling HTTP endpoints.

## Publish

```powershell
dotnet publish -c Release
```

Deploy the contents of:

```text
bin/Release/net8.0/publish/wwwroot
```

The host must serve `index.html` for unknown client-side routes such as `/vault`, `/add`, and `/system`.

Example Nginx fallback:

```nginx
location / {
    try_files $uri $uri/ /index.html;
}
```

## Horizon CORS

The browser calls Horizon directly. Horizon or its reverse proxy must allow the PWA origin.

Example:

```nginx
add_header Access-Control-Allow-Origin "https://vault.example.com" always;
add_header Access-Control-Allow-Methods "GET, POST, OPTIONS" always;
add_header Access-Control-Allow-Headers "Content-Type, Authorization" always;

if ($request_method = OPTIONS) {
    return 204;
}
```

Do not use `*` when credentials or other protected browser features are introduced. Restrict the origin to the deployed PWA domain.

## Ledger format

```text
sv:next                         -> raw 32-byte public key of the next account
svh:<16-char-record-id>         -> 47-byte committed manifest
svc:<record-id>:0000            -> encrypted data chunk
svc:<record-id>:0001            -> encrypted data chunk
...
```

The Blazor client uses `window.crypto.subtle` so the web format remains byte-compatible with the console application. The encrypted envelope is:

```text
version (1 byte)
nonce   (12 bytes)
tag     (16 bytes)
ciphertext
```

The manifest stores the chunk count, encrypted length, SHA-256 ciphertext hash, and update timestamp. The header is submitted last, so incomplete chunks are ignored by readers.

## Security notes

- This is an experimental prototype and has not been professionally audited.
- All ciphertext is public on a public Stellar network and can remain in historical records after replacement.
- Anyone who obtains the root secret can sign database operations and derive the vault encryption key.
- The app never stores the root secret in `localStorage`, IndexedDB, cookies, or application settings.
- The secret and decrypted records exist in WebAssembly memory while the vault is open.
- Clipboard history, browser extensions, screenshots, terminal inspection, and a compromised device can expose revealed data.
- Use HTTPS for production.
- Configure an appropriate child balance for the network base reserve and chosen data-entry limit.

## Compatibility with the console prototype

The PWA uses the same entry names, manifest format, encryption derivation, chunking, and child-account chain design as the supplied console application. Existing records should be readable when the same root secret and network passphrase are used.

## Docker

```powershell
docker build -t stellar-vault-pwa .
docker run --rm -p 8080:80 stellar-vault-pwa
```

Open `http://localhost:8080`. For production, put the container behind an HTTPS reverse proxy.
