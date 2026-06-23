window.stellarVault = (() => {
    const cryptoContexts = new Map();
    const encoder = new TextEncoder();

    function requireContext(contextId) {
        const context = cryptoContexts.get(contextId);
        if (!context) {
            throw new Error('The browser encryption context is unavailable. Lock and reopen the vault.');
        }
        return context;
    }

    async function createContext(contextId, seedBytes, passphrase, accountId) {
        if (!window.crypto?.subtle) {
            throw new Error('This browser does not provide the Web Crypto API.');
        }

        const seed = new Uint8Array(seedBytes);
        try {
            const baseKey = await crypto.subtle.importKey(
                'raw',
                seed,
                'HKDF',
                false,
                ['deriveKey']);

            const saltMaterial = encoder.encode(`stellar-vault/salt/v1|${passphrase}|${accountId}`);
            const salt = await crypto.subtle.digest('SHA-256', saltMaterial);
            const info = encoder.encode('stellar-vault/aes-256-gcm/v1');
            const key = await crypto.subtle.deriveKey(
                {
                    name: 'HKDF',
                    hash: 'SHA-256',
                    salt,
                    info
                },
                baseKey,
                {
                    name: 'AES-GCM',
                    length: 256
                },
                false,
                ['encrypt', 'decrypt']);

            cryptoContexts.set(contextId, {
                key,
                aadPrefix: `stellar-vault|v1|${passphrase}|${accountId}|`
            });
        } finally {
            seed.fill(0);
        }
    }

    async function encrypt(contextId, recordId, plaintextBytes) {
        const context = requireContext(contextId);
        const nonce = crypto.getRandomValues(new Uint8Array(12));
        const plaintext = new Uint8Array(plaintextBytes);
        const aad = encoder.encode(context.aadPrefix + recordId);

        const encryptedWithTag = new Uint8Array(await crypto.subtle.encrypt(
            {
                name: 'AES-GCM',
                iv: nonce,
                additionalData: aad,
                tagLength: 128
            },
            context.key,
            plaintext));

        const ciphertextLength = encryptedWithTag.length - 16;
        const envelope = new Uint8Array(1 + 12 + 16 + ciphertextLength);
        envelope[0] = 1;
        envelope.set(nonce, 1);
        envelope.set(encryptedWithTag.subarray(ciphertextLength), 13);
        envelope.set(encryptedWithTag.subarray(0, ciphertextLength), 29);

        plaintext.fill(0);
        encryptedWithTag.fill(0);
        return envelope;
    }

    async function decrypt(contextId, recordId, envelopeBytes) {
        const context = requireContext(contextId);
        const envelope = new Uint8Array(envelopeBytes);

        if (envelope.length < 29) {
            throw new Error('The encrypted record is too short.');
        }
        if (envelope[0] !== 1) {
            throw new Error(`Unsupported encrypted record version: ${envelope[0]}.`);
        }

        const nonce = envelope.slice(1, 13);
        const tag = envelope.slice(13, 29);
        const ciphertext = envelope.slice(29);
        const cipherWithTag = new Uint8Array(ciphertext.length + tag.length);
        cipherWithTag.set(ciphertext, 0);
        cipherWithTag.set(tag, ciphertext.length);
        const aad = encoder.encode(context.aadPrefix + recordId);

        try {
            return new Uint8Array(await crypto.subtle.decrypt(
                {
                    name: 'AES-GCM',
                    iv: nonce,
                    additionalData: aad,
                    tagLength: 128
                },
                context.key,
                cipherWithTag));
        } finally {
            cipherWithTag.fill(0);
            ciphertext.fill(0);
            tag.fill(0);
            nonce.fill(0);
        }
    }

    async function sha256(valueBytes) {
        const value = new Uint8Array(valueBytes);
        return new Uint8Array(await crypto.subtle.digest('SHA-256', value));
    }

    function randomHex(byteCount) {
        if (!Number.isInteger(byteCount) || byteCount < 1 || byteCount > 1024) {
            throw new Error('Invalid random byte count.');
        }

        const bytes = crypto.getRandomValues(new Uint8Array(byteCount));
        return Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
    }

    function generatePassword(length) {
        const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*_-+=?';
        const requestedLength = Math.max(12, Math.min(Number(length) || 24, 128));
        const maximumAcceptedByte = 256 - (256 % alphabet.length);
        let output = '';

        while (output.length < requestedLength) {
            const random = crypto.getRandomValues(new Uint8Array(requestedLength * 2));
            for (const value of random) {
                if (value >= maximumAcceptedByte) {
                    continue;
                }
                output += alphabet[value % alphabet.length];
                if (output.length === requestedLength) {
                    break;
                }
            }
            random.fill(0);
        }

        return output;
    }

    return {
        getLocal: key => window.localStorage.getItem(key),
        setLocal: (key, value) => window.localStorage.setItem(key, value),
        copyText: async value => {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(value);
                return;
            }

            const textArea = document.createElement('textarea');
            textArea.value = value;
            textArea.style.position = 'fixed';
            textArea.style.opacity = '0';
            document.body.appendChild(textArea);
            textArea.focus();
            textArea.select();
            document.execCommand('copy');
            textArea.remove();
        },
        setTheme: theme => {
            const normalized = theme === 'amber' ? 'amber' : 'green';
            document.documentElement.dataset.theme = normalized;
            window.localStorage.setItem('stellar-vault.theme.v1', normalized);
        },
        getTheme: () => window.localStorage.getItem('stellar-vault.theme.v1'),
        crypto: {
            createContext,
            deleteContext: contextId => cryptoContexts.delete(contextId),
            encrypt,
            decrypt,
            sha256,
            randomHex,
            generatePassword
        }
    };
})();
