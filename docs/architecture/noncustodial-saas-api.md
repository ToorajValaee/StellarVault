# Public StellarVault storage API / SaaS proposal

Status: **storage/SaaS architecture proposal; a self-hosted read-only HTTP preview now exists in `src/StellarVault.Api` (see `docs/API.md`). No deployed public storage API or custody backend exists.** This is a low-risk extension to the existing Core; do not block the Testnet application pilot on SaaS implementation.

## Two explicit storage modes

- **single-account**: use one caller-controlled Stellar G-address as the complete storage boundary. No child account creation, signer modification, or implicit tail expansion. If the record/file cannot fit the account's available storage budget, return a capacity/fee error; never silently turn on chaining.
- **linked-chain**: caller explicitly opts in to the independent linked-account strategy. Child-account creation requires a separate verified capacity quote, user authorization, valid root/child signature evidence and fee/reserve approval. Existing root/child signing policy stays unchanged.
- The HD mnemonic strategy is an *independent identity strategy*, not a fallback secret for either mode. A caller can derive its own signer and authorize single-account storage, but must never be required to disclose a mnemonic or imported S-key to a hosted API.

## Trust and key model

1. Prefer **noncustodial architecture**: plaintext and encryption keys remain on the user's device. Client-side SDK encrypts content, constructs the exact ledger intent, validates the network, and signs locally. The service sees public account IDs, encrypted payloads, transaction bytes and necessary public status data only.
2. Separate API-key/OAuth SaaS access (quota, invoices, rate limits) from Stellar account ownership. API authentication is not blockchain signing authorization.
3. A hosted relay may accept a **fully signed, network-pinned, hash-pinned** transaction envelope; it must not accept a secret key to sign on the user's behalf. No silent transaction resubmission on timeout, ambiguity or crash.
4. Never allow client-provided arbitrary Horizon URLs or network passphrases in a trusted signing/verification channel; use allowlisted network configurations and pin the exact signed envelope hash.
5. Use dedicated scoped operation IDs, durable idempotency journals, signed-submission checkpoints, authenticated customer/tenant isolation and independently checked historical receipt/operation/signer proofs before marking completion.
6. If an enterprise customer explicitly requests custodial keys, that is a **different regulated product** requiring HSM/KMS, key-recovery policy, security audit and legal review. It must not inherit the noncustodial API trust claim.

## Proposed surface (not implemented)

- `POST /v1/storage/quotes`: estimate size, operation count, fees, reserves, capacity and any chain expansion for mode `single-account` or `linked-chain`. Pure read-only, no mutation.
- `POST /v1/storage/records/prepare`: accept encrypted typed record/folder metadata, explicit owner, network and revision/precondition; return a one-shot signable intent and its hash.
- `POST /v1/storage/files/prepare`: initiate SVTREE3 encrypted streaming-node staging and an explicit final manifest plan; no plaintext uploads or whole-file buffering.
- `POST /v1/storage/submit`: accept exact approved signed envelope and durable operation ID; commit checkpoint before broadcast. Ambiguous outcomes remain `uncertain` without automated retry.
- `GET /v1/storage/operations/{id}`: safe status and signed-history provenance; no optimistic success claims.
- `GET /v1/storage/records/{id}`, `GET /v1/storage/files/{id}/manifest` and `GET /v1/storage/files/{id}/nodes`: return encrypted ledger bytes and integrity information for client-side verification/decryption.
- `POST /v1/storage/linked/quotes`: explicit account-chain expansion review, not a side effect of a single-account save.
- `GET /v1/storage/capacity`: current fee/reserve/capacity snapshot with freshness timestamp.

## Architecture reuse

Expose small public, testable Core contracts for record envelopes, SVTREE3 manifests, strategy-aware quote planning and proof-gated transaction reconciliation. An ASP.NET Core API adapter can call those services. Keep fee/revision/strategy logic in Core and keep hosted authentication, billing, API keys, observability and rate limiting in the server adapter. PWA, MAUI and future SDKs continue to share one domain layer and distinct HD/S-key signing policy.

Support tenant quotas, record/file size/number and rate budgets; disclose network transaction and storage charges separately from SaaS pricing. Practical Stellar ledger capacity, transaction costs, reserves, network latency, Horizon limits and upload lifecycle make literally unlimited on-chain storage impossible.

## Required before a public SaaS release

- Exact-head automated Release build and strong Core/API threat tests, including signature/network mismatch, replay, idempotency, authorization bypass, root/child strategy confusion, tenant crossover and timestamp/rate-limit manipulation.
- Controlled Testnet adversarial tests with synthetic credentials, independent signed-XDR proofs, crash-before/after-submit recovery, multi-device revision conflicts and full-stream file reconstruction.
- Independent protocol and penetration testing, privacy/DPA review, retention/deletion policy, billing transparency, abuse handling, availability/SLA, deployment and rollback runbooks.
- Document which metadata is public on Stellar, why ciphertext/record size cannot be deleted retroactively from ledger history, and who controls keys and network fees.

No API key or OAuth token alone authorizes a Stellar mutation; **the user's exact cryptographic approval is always required**.
