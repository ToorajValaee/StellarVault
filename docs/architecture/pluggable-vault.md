# Pluggable vault architecture (2026-11-14)

## Core invariant

Core manages logical vault content, encrypted persistence, record versions, conflict rules, and explicit user-approved synchronization. A blockchain adapter is responsible for chain transactions, authorization, fees, confirmations, and verification. No adapter can assert remote commit from a local encrypted save. Never treat Horizon receipts alone as historical record proof.

## Orthogonal choices

1. **Account provisioning strategy** selects how an account is created/discovered/recovered. Stellar HD (SEP-5) and imported S-key linked accounts are independent first-class strategies. The linked strategy must verify child creation, configured parent/root signer weight and threshold, funding/reserves, and chain registry before reporting readiness. Reuse the current JS workflows only behind a verified adapter; no key material in account handles.
2. **Record type factory** selects stable logical types: text, file, folder, credential, later extensible types. MIME type and filename are *hints*, not trustworthy executable semantics. Unknown types remain opaque, not auto-previewed. The new factory is domain-only: it does not persist, encrypt, or serialize records.
3. **Storage mode** independently selects on-chain, opted-in encrypted local, or hybrid workflows. A local save must never imply on-chain publication. Encrypted chunk storage and authenticated indexes remain shared; chain-specific publication is adapter-owned.
4. **Blockchain adapter** determines capabilities, signer and transaction construction, record transport, fees, and finality semantics. EVM needs different implementations for HD derivation, smart-contract storage and programmable authorization; it cannot be implemented by substituting an EVM keypair into Stellar operations.

## Compatibility decisions

- Existing local catalogs currently distinguish text from files only by ContentType. A null content type is historical text; `text/plain` and `text/plain;...` are text. The new `VaultRecordTypeFactory.ClassifyLegacy` preserves this mapping. Do not rewrite stored ciphertext or indexes as part of the type registry introduction.
- Existing on-chain HD ledger and imported S-key JS workflows are not migrated by this change. No live strategy is registered yet. Do not call the new abstractions complete integrations or enable any Mainnet flow.
- Folder hierarchy, content type metadata, encrypted file chunks and pointers remain as-is. When a future versioned record envelope is introduced, add logical type ID, type schema version, canonical content digest, encrypted metadata, parent relation, and explicit optional file media descriptors. Unknown future types must roundtrip without reinterpretation.

## Future sharing and portable import/export

Sharing must be **explicitly authorized**, recipient-bound, revocable where the transport allows, and use new recipient-specific wrapping keys or grants. Never share a mnemonic, imported S-key, account signer key, or the master encryption key. A share link is not proof of permission; authenticate identities, scope, expiry, changes, and revocation.

Export must use an authenticated, versioned format with stable logical record IDs/types, encrypted payloads and manifests, chunk integrity, provenance/network context, and documented KDF parameters. Import must parse untrusted data under strict size/format limits, verify authentication before plaintext release, handle unknown types safely, and use conflict-aware remapping (including folder references). Cross-chain export/import must not pretend an EVM receipt is a Stellar transaction. Neither workflow is implemented by these contracts.

## Next implementation increments

- Wrap existing SEP-5 and S-key provisioning/discovery with concrete `IVaultAccountProvisioningStrategy` implementations, without changing existing signing paths.
- Route existing text/file creation through the record factory and retain legacy classification until schema migration is designed and tested.
- Introduce chain adapters behind current ledger transport and establish a cross-chain-independent encrypted record envelope.
- Build authenticated sharing and portable import/export **after** versioned metadata, recovery, and remote commit verification are stable.
