# Vault data-key wrapping (implementation stage)

`VaultKeyEnvelope.GenerateDataKey()` creates a random 32-byte AES-256 vault data key. `Wrap(dataKey, wrappingKey, stellarAccountId)` encrypts it using AES-256-GCM, a fresh 96-bit nonce, and authenticated account binding. `Unwrap(envelope, wrappingKey, stellarAccountId)` returns the original key only after authentication. Format is SVKW magic, version 1, nonce, 32-byte encrypted key and 16-byte tag.

The wrapping key **must** be 32 cryptographically random or independently derived bytes from an approved key derivation and key management workflow. Never pass a raw passphrase, Stellar seed, or Stellar secret key as a wrapping key. This module deliberately does not implement password derivation; a vetted Argon2id library and explicit KDF parameters are still required before passphrase unlock can ship.

Store the envelope as confidential application metadata; an envelope alone is not an account-ownership proof. The application must validate Stellar StrKey checksums and account authorization separately. Clear temporary key buffers after use; managed runtime copies and GC mean zeroization is best-effort. Key rotation can rewrap the same data key without rewriting every encrypted chunk. Do not consider the overall unlock flow production ready until the KDF, secure key lifecycle, and account authorization are integrated and validated.
