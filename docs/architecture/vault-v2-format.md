# StellarVault v2 encrypted envelope (work in progress)

This design intentionally **does not read or migrate older vault formats**. The v2 envelope is currently a Core cryptographic building block, **not yet connected to vault persistence or mnemonic handling**.

## Binary layout

| Offset | Size | Value |
| --- | --- | --- |
| 0 | 4 | ASCII `SVLT` |
| 4 | 1 | Version byte `2` |
| 5 | 12 | Random AES-GCM nonce |
| 17 | variable | Encrypted bytes |
| end - 16 | 16 | AES-GCM authentication tag |

The complete five-byte header and caller-supplied context (length-prefixed) are authenticated as associated data. Unknown versions, invalid magic, truncated payloads and authentication failures are rejected. Each encryption generates a fresh random 96-bit nonce; the caller must ensure a key is not used for excessive numbers of encryptions.

## Security boundary and remaining work

- Encryption requires a caller-provided 32-byte key. **No password-to-key derivation has been implemented**. Select and benchmark a maintained Argon2id implementation for each supported runtime before integrating passwords.
- Define and test a serialized vault record with an authenticated KDF configuration, random salt, and bounded work parameters. Reject resource-exhaustion values before deriving keys.
- Separate vault encryption keys from account signing keys, use purpose/domain separation and platform-protected storage where available.
- Define memory clearing, auto-lock, error handling, and recovery semantics across browser and MAUI hosts.
- Execute tests on browser and native devices, and arrange a security review before storing real mnemonic material.

The versioned envelope is deliberately not a claim of production-ready wallet encryption.
