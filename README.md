# StellarVault

A .NET 10, Stellar-account-centered encrypted vault/database. The native **.NET MAUI Blazor Hybrid** app and **Blazor WebAssembly PWA** share Core services and Razor UI. StellarVault supports a BIP-39/SEP-5 mnemonic HD architecture and is implementing a separately versioned **Stellar Secret Key / linked-account** architecture. They are independent credential paths, not different passwords for one HD wallet. **Automatic legacy vault migration is not supported.**

## Repository structure

```text
StellarVault.sln
src/
  StellarVault.Core/       Encrypted database, records, account authorization contracts
  StellarVault.UI/         Shared Razor components
  StellarVault.Maui/       Android, iOS, Windows, Mac Catalyst native host
  StellarVault.Pwa/        Browser/WebAssembly host, IndexedDB, PWA assets
tests/
  StellarVault.Core.CryptoTests/
docs/
  architecture/
website/                  Independent landing site
.github/workflows/        Cross-platform CI
Dockerfile                .NET 10 browser build and Nginx hosting
```

## Run the browser preview

```bash
dotnet restore src/StellarVault.Pwa/StellarVault.Pwa.csproj
dotnet run --project src/StellarVault.Pwa/StellarVault.Pwa.csproj
```

## Native development

Open `StellarVault.sln` and select `StellarVault.Maui` as the startup project. Install .NET 10 MAUI workload and the target platform toolchain. Build a target framework explicitly, for example `dotnet build src/StellarVault.Maui/StellarVault.Maui.csproj -f net10.0-android`.

## Secret Key linked-account Testnet preview

The imported Stellar S-key path uses a distinct on-chain record format and authorizes independently created child accounts with the root public key as a signer. The same UI component and C# transport run in PWA and MAUI. Testnet encrypted records/folders can be reviewed for create, update, delete and chain extension. Each operation requires a separate fee/reserve quote and explicit approval; no automatic transaction retries occur after an uncertain outcome.

**Secret Key encrypted offline record/folder drafts are implemented** with separately consented, account/network-scoped local keywrap, editable encrypted queue entries, individual fee-reviewed create/update/delete publication, revision-pinned original manifests for existing records, and durable expansion intents. Offline-only queue unlock cannot publish. Linked queue mutations remain `Uncertain` or hash-pinned `Submitting` until the user explicitly verifies a matching successful Horizon receipt, the exact root-signed XDR, decrypted historical record bytes (or dual-signed linked child expansion), and a fresh current-state check; only then can an encrypted CAS mark the original mutation `Confirmed`. On uncertain Horizon submission, the signed transaction hash is captured before broadcast when possible, stored durably as a public recovery handle and usable for read-only receipt lookup after reopening the vault; this is **not proof of operation content and cannot unblock the queue**.

**Still incomplete:** full-size S-key multipart files and crash-safe multipart deletion (single-record SVFILE1 up to 2 KiB and a new bounded SVFILE2 manifest-last upload path up to 96 KiB are implemented), durable direct-mode uncertainty recovery without local consent, legacy pre-HD vault migration, extensive live Testnet interruption/cross-device validation, independent security audit and production release hardening. Cryptographically proof-gated reconciliation is now implemented for individually queued record/folder creates, updates, deletes and linked expansions, but its real-network safety is not yet validated. Linked queued update/delete is implemented with online-only revision capture, encrypted local staging, a fresh fee quote, signed-hash checkpointing and fail-closed stale-version checks. Queued transactions remain quarantined until the explicit signed-history reconciliation step succeeds. No automatic retry or publication of a second transaction is permitted while an earlier one is unverified. Mainnet S-key mutations are blocked. HD and S-key remain separate. See [docs/SECRET_KEY_CHAIN_OPTION.md](docs/SECRET_KEY_CHAIN_OPTION.md) and the [controlled Testnet pilot restrictions](docs/verification/controlled-testnet-pilot.md).

Relevant non-network protocol regression tests:

```bash
node --test tests/secret-key-chain.test.cjs tests/secret-key-ledger.test.cjs
```

## Account-backed object engine (current branch)

The chain-neutral `VaultEngine.Core` contracts define scoped Get, authenticated
OpenRead, staged Put/Update/Delete, verified streamed exports, cost estimates and
explicit mutation reviews. `StellarAccountObjectStoreProvider` is the first
provider; HD/mnemonic and independent Secret Key accounts use their existing
distinct signers and encrypted journals. **Preparing a mutation never publishes
it.** Deletes for ordinary records and empty folders are pinned to the exact
revision returned by engine Get, then revalidated inside the underlying
journal. File trees are intentionally excluded from ordinary Delete.

The browser HD and shared/native/linked explorers expose import from the vault
menu and per-record export from each record's context menu and details pane.
Exports re-read the connected ledger, require explicit plaintext consent (or
a separately password-encrypted backup), and reject a selected record that is
missing or no longer an ordinary record.

For SVTREE3 file trees, authenticated reads now enforce the same file-group tag
on every node rather than trusting checksums alone. A chain-neutral,
read-only `IVerifiedObjectNodeInventory` can report a fully verified
root-first node inventory after streaming verification without materializing
plaintext in memory. It is a **snapshot for planning only**, not permission
to delete, a current-state lock, or a signed transaction receipt. PWA/MAUI
register the same capability. Recovery inspection can surface **all signed
checkpoint hashes** for a multi-owner HD deletion or multipart file upload;
every checkpoint still requires independent historical verification.

The engine's file metadata `GetAsync` now verifies the complete SVTREE3 tree
and content hash into a discard sink, without allocating the file as plaintext.
`OpenReadAsync` remains capped at 8 MiB for verified in-memory reads;
`ExportVerifiedAsync` is the private-destination streaming path for larger files.

**Not yet implemented or release-approved:** whole-tree deletion publication
with restart/reconciliation proofs; unrestricted large-file use; independent
live Testnet interruption verification, cross-platform release testing, and
third-party cryptographic/security audit. These are blockers, not compatibility
features. EVM is a future provider and must not be emulated through Stellar
account identifiers or Stellar fee assumptions.

For the focused static regression suite:
```bash
node --test tests/account-object-store-stellar-provider.test.cjs tests/record-interchange.test.cjs
```
Follow it with full `dotnet build`, Core crypto tests, PWA publish and the
manual `PWA-RELEASE-GATE.md` checklist before any release decision.

## Security and release readiness

**Not yet production-ready.** Complete .NET 10 builds, platform testing, live Stellar Testnet recovery and independent security review before entering secrets protecting real funds. Current-state ledger recovery and mocked transaction receipts alone are not production proof. Do not use this preview with valuable secrets.

The landing site in `website/` is independently maintained.
