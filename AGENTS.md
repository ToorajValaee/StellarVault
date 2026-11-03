# AI coding guidance — StellarVault

Read [docs/ENGINEERING_HANDOFF.md](docs/ENGINEERING_HANDOFF.md) before modifying code. Update it with each meaningful repository change. Keep this file aligned with the product architecture and unresolved blockers.

## Architecture and product contract

- Shared .NET 10 domain/services: `src/StellarVault.Core`; PWA host: `src/StellarVault.Pwa`; reusable UI: `src/StellarVault.UI`.
- Stellar on-chain vault is the primary mode. Optional encrypted IndexedDB local mode must be explicitly enabled. Do not silently switch to local persistence or broadcast transactions.
- Preserve the original on-chain `/vault` experience and account/network isolation. Keep mnemonic/keys out of logs, URL parameters and unencrypted persistent storage.
- Offline mutation queues are encrypted at rest, account/network scoped, and use compare-and-swap durable state transitions. Never retry Submitting/Uncertain mutations blindly.

## Safety-critical Sync rules

- The manual publishing coordinator exists but is **not exposed in the UI**. Publication requires fresh per-mutation explicit approval, remote conflict checks, and fee/reserve review.
- The default `IVaultLedgerMutationVerifier` intentionally rejects confirmation. **Do not replace it** based solely on a successful Horizon receipt or a matching `svh:<recordId>` ManageData manifest operation.
- HD-v2 writer `src/StellarVault.Pwa/LegacyLedger/StellarVaultStore.cs` writes encrypted `svc:` chunks then a `svh:` manifest, potentially across multiple transactions, and returns only the last hash. Independent confirmation still requires network/account provenance, chunk integrity, and matching decrypted record fields.
- Horizon inspection utilities are read-only diagnostics; never interpret their results as an authorization to mark queued mutations Confirmed.

## Developer workflow

- Prefer complete end-to-end implementation batches instead of isolated cosmetic checks.
- Review the existing source/API contracts before edits; preserve compatibility for future MAUI sharing.
- Current user preference is to defer builds/tests until explicitly requested. State clearly when validation has not run; never claim production readiness without evidence.
- Keep docs and AI handoff truthful: distinguish implemented source from integration-tested/production-ready behavior.

## Current Horizon evidence boundaries (2026-11-03)

- `HorizonVaultManifestInspector.InspectManifestAsync` validates the 51-byte HD-v2 manifest envelope and returns metadata, **not** complete independent publication proof.
- `HorizonVaultPublicationDiagnostic.InspectForNodeAsync` optionally checks the **operation** source account against the expected HD child. A sponsored transaction's receipt source may be different.
- Manifest metadata (including ciphertext digest and generation) is only a commitment; verify historical `svc:` writes, their account lineage, SHA-256 assembled ciphertext, expected decrypted record fields, and network identity before considering any confirmation integration.
- Never confuse `ManifestFound` with a verified queue mutation. Fail closed on ambiguous/missing operations and incomplete pages. The default verifier and UI publishing feature remain disabled.
