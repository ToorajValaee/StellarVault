# Stellar Vault PWA — Browser SDK edition

A mobile-first Blazor WebAssembly PWA that stores AES-GCM encrypted credential records in classic Stellar `ManageData` entries and automatically extends storage with linked child accounts.

## Important browser fix

This edition does **not** reference `stellar-dotnet-sdk` or `NSec.Cryptography` from the WebAssembly client. NSec depends on platform-specific native cryptography and cannot initialize inside the browser WebAssembly sandbox.

Instead:

- Razor UI, vault parsing, chunking, manifests, and application state remain in C#.
- Stellar key derivation, Ed25519 signing, transaction construction, Horizon calls, and child-account creation use the browser build of `@stellar/stellar-sdk` through JavaScript interop.
- HKDF-SHA256 and AES-256-GCM use the browser Web Crypto API.
- The root secret is not sent to an application server.

The vendored browser bundle is:

```text
wwwroot/js/vendor/stellar-sdk.min.js
@stellar/stellar-sdk 16.0.1
```

## Requirements

- .NET 8 SDK
- A modern browser with WebAssembly and Web Crypto
- A Horizon server that permits the PWA origin through CORS
- HTTPS in production; `localhost` is acceptable during development

## Run

```powershell
cd StellarVaultPwa-browser-sdk-fix
dotnet clean
Remove-Item -Recurse -Force bin, obj -ErrorAction SilentlyContinue
dotnet restore
dotnet run
```

After replacing an older installed PWA, clear the old service worker/cache or use the browser's **Clear site data** command once.

## Architecture

```text
Blazor WebAssembly UI
        │
        ├── C#: record model, manifests, chunking, chain reconstruction
        ├── Web Crypto: HKDF, AES-GCM, SHA-256
        └── Stellar JS SDK: Ed25519, XDR, Horizon, signing, submission
```

## Network support

The access screen supports:

- Stellar Testnet
- Stellar Public network
- Custom network name
- Custom Horizon URL
- Exact custom network passphrase
- HTTP Horizon during local development

A page served over HTTPS cannot call an HTTP Horizon endpoint because browsers block mixed content.

## Security notes

- The root secret is kept in the active browser session and is not intentionally written to local storage.
- Network settings are stored locally.
- Ciphertext written to a public ledger is permanently observable and may remain recoverable from history.
- Anyone who obtains the root secret can decrypt the vault and authorize writes for all linked child accounts.
- The vendored JavaScript SDK should be updated deliberately and reviewed before changing versions.
- This is an experimental prototype and has not received a security audit.
