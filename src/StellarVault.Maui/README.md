# StellarVault MAUI Blazor Hybrid host

**Status: preview scaffold only — not a functioning vault, not build-verified.**

This is one native host targeting Android, iOS, Mac Catalyst and Windows (Windows target included when building on Windows). It references the shared `StellarVault.UI` Razor Class Library and `StellarVault.Core`.

The initial native screen is **intentionally non-functional**: no mnemonic input, key derivation, ledger calls, or local secret persistence. Do not expose vault controls until the existing browser crypto bridge, vault session and storage abstractions have been migrated and tested for cross-platform compatibility.

## Build prerequisites
Install the .NET 8 MAUI workloads and the relevant Android/iOS/Mac Catalyst/Windows toolchains. Apple targets require compatible macOS tooling. Example Android build on a configured machine:

```bash
dotnet workload install maui
dotnet build src/StellarVault.Maui/StellarVault.Maui.csproj -f net8.0-android
```

These commands have **not** been executed in this environment. Version and platform configuration may need adjustment after the first native CI build.

## Release gates
- UI, session and browser/native crypto compatibility tests.
- Test vectors for SEP-0005 and legacy account derivation.
- Encrypted record interoperability and cross-device recovery.
- Clear network cost approvals for all ledger writes.
- Native secret lifecycle and platform security review.

See [Step 05](../../docs/steps/05-maui-host.md).


## Native vault session lifecycle

The MAUI host registers `VaultSessionManager` as a singleton and locks its active session on the window `Stopped` and `Destroying` events. This is defense in depth for future unlock integration; the native preview still disables secrets and transactions. Platform-specific lifecycle handling, timeout locking, secure passphrase derivation, and end-to-end validation remain required before enabling the vault. The session manager is not the existing browser `IVaultOperations` service; no claim of complete UI migration is made.

## Stellar ownership authorization integration (pending)

The shared core provides `StellarChallengeAccountAuthorizer` and two required external contracts:

- `IStellarChallengeSigner`: have an external wallet sign the exact domain-separated challenge bytes without exporting secrets.
- `IStellarOwnershipProofVerifier`: independently fetch fresh signer/threshold data for the selected Stellar account and verify the exact Ed25519 signature and applicable signer policy. Do not trust signer lists from the client.

Challenges include a random 32-byte nonce, the account G-address, selected network passphrase and a two-minute expiry. The challenge coordinator is not currently registered by MAUI: `DenyVaultAccountAuthorizer` remains the default. Consequently the create/unlock form intentionally refuses authorization. Before enabling it, implement and validate both contracts, signer-weight/threshold policy, correct network identity, wallet compatibility, and replay handling on the trusted verification boundary. A single signed challenge is a proof of signer control, not proof of ownership of an arbitrary account or authorization for a Stellar transaction.

The development UI must not be treated as production-ready or enabled by substituting an always-true verifier.

### Ledger verifier added

The shared core now includes `StellarLedgerOwnershipVerifier`, which fetches current account signers from a configured HTTPS Horizon server and requires an Ed25519 signer with weight meeting the account's **high threshold**. It deliberately rejects multisig scenarios where no single signer independently meets that threshold. It relies on an injected `IStellarEd25519SignatureVerifier` for real Ed25519 verification and must only be configured with a pinned/trusted Horizon endpoint matching the declared network. A public Horizon endpoint alone is not an authenticated proof of network identity; endpoint trust and freshness/reorg policy need review.

**Not enabled:** The native host continues to register `DenyVaultAccountAuthorizer`. Before switching it, implement a vetted Ed25519 verifier, wallet signing bridge, precise challenge-payload validation/replay policy, signer-change race handling, and live integration tests. No fallback may authorize an account merely from a supplied G-address.
