# StellarVault cross-platform migration

## Goal
Share one Razor UI and one vault engine between the browser (Blazor WebAssembly) and native applications (.NET MAUI Blazor Hybrid). The public landing site is independently deployed from `website/`.

## Target layout
```text
website/                    Public static landing site; separate deployment
src/StellarVault.Core/       Platform-independent vault models, services, interfaces
src/StellarVault.UI/         Razor Class Library: pages, components, layouts, styling
src/StellarVault.Web/        Thin browser host for app.svault.ir
src/StellarVault.Maui/       Thin MAUI Blazor Hybrid host for Android/iOS/Windows/macOS
```

## Migration stages
1. Import and independently deploy the existing static landing site under `website/`.
2. Move portable models and interfaces to `StellarVault.Core`, retaining the current serialization contract.
3. Extract Razor UI into `StellarVault.UI` with both hosts referencing it.
4. Move Stellar/HD crypto implementations behind interfaces; ensure the browser and native implementations derive **identical keys** and produce compatible ciphertext.
5. Introduce the MAUI host; implement secure local storage, file access and WebView/JS interop where required.
6. Validate new and legacy derivation, unlock, write, read, folder navigation, backup and cross-device recovery.
7. Switch browser hosting to `StellarVault.Web` only after parity tests pass; keep existing production project intact until then.

## Compatibility and security invariants
- Never log or transmit mnemonics, derived private keys, passphrases or plaintext credentials.
- Preserve SEP-0005 and legacy BIP32-compatible derivation behavior; do not silently re-derive account keys.
- Preserve encrypted record formats, associated data, registry layout and Stellar account ownership.
- Verify the same existing vault can be unlocked and modified across browser and native targets.
- All Stellar writes continue to require clear user cost approval.
- No production deployment or migration is implied by adding the scaffold.

## Build environments
MAUI Android/iOS/macOS workloads require compatible .NET SDK/workloads and platform-specific build environments. iOS/macOS builds require macOS tooling. The browser host remains a standard WebAssembly deployment.
