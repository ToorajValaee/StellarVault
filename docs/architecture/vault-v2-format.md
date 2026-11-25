# StellarVault v2 encrypted envelope

This design intentionally **does not read or migrate older vault formats**. The v2 envelope is implemented in `VaultEngine.Stellar` and used by encrypted local storage. It is distinct from the HD and linked on-chain record envelopes; it does not replace their derivation or ledger formats.

## Binary layout

| Offset | Size | Value |
| --- | --- | --- |
| 0 | 4 | ASCII `SVLT` |
| 4 | 1 | Version byte `2` |
| 5 | 12 | Random AES-GCM nonce |
| 17 | variable | Encrypted bytes |
| end - 16 | 16 | AES-GCM authentication tag |

The current envelope limits plaintext to 1 MiB and associated context to 1 KiB. Oversized encrypted inputs are rejected before decryption or large allocations. These are deliberately conservative limits for mnemonic-oriented vault data, not general-purpose file encryption.

The complete five-byte header and caller-supplied context (length-prefixed) are authenticated as associated data. Unknown versions, invalid magic, truncated payloads and authentication failures are rejected. Each encryption generates a fresh random 96-bit nonce; the caller must ensure a key is not used for excessive numbers of encryptions.

## Integration and security boundary

- `VaultV2Envelope` implements this bounded authenticated envelope; local chunk stores validate its lengths.
- `VaultPassphraseKdf` implements Argon2id for passphrase-derived wrapping keys. Defaults are a random 16-byte salt, 64 MiB memory, three iterations and parallelism one. Versioned parameters are bounded before derivation.
- `VaultPassphraseCredentials` serializes KDF parameters and the authenticated wrapped data key. `VaultPassphraseLifecycle` and `VaultSessionWorkflow` implement credential creation/unlock and session ownership.
- Local namespace unlock is not Stellar signing authorization. HD and independent S-key ledger sessions remain separate.
- PWA local retention is explicit. Session lock and encrypted transaction-recovery metadata do not authorize automatic retries or queued mutation confirmation.

Implementation and executable local tests exist; independent cryptographic assessment and release acceptance are separate evidence. See [current release status](../verification/current-release-status.md).
