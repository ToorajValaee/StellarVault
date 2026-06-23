window.stellarVault = (() => {
    const stellarSessions = new Map();
    const encoder = new TextEncoder();

    function requireStellarSdk() {
        if (!window.StellarSdk) {
            throw new Error('The browser Stellar SDK failed to load. Reload the application and check the browser console.');
        }
        return window.StellarSdk;
    }

    function requireSession(sessionId) {
        const session = stellarSessions.get(sessionId);
        if (!session) {
            throw new Error('The browser Stellar session is unavailable. Lock and reopen the vault.');
        }
        return session;
    }

    function normalizeHorizonUrl(value) {
        const url = String(value || '').trim().replace(/\/+$/, '');
        if (!url) {
            throw new Error('Horizon URL is required.');
        }
        return url;
    }

    function sdkBufferFromBase64(session, value) {
        const BufferType = session.keypair.rawPublicKey().constructor;
        return BufferType.from(value, 'base64');
    }

    function formatStellarError(error) {
        const responseData = error?.response?.data;
        const extras = responseData?.extras;
        const resultCodes = extras?.result_codes;

        if (resultCodes) {
            const operations = Array.isArray(resultCodes.operations)
                ? ` Operations: ${resultCodes.operations.join(', ')}.`
                : '';
            return `Stellar transaction failed: ${resultCodes.transaction || 'unknown'}.${operations}`;
        }

        if (responseData?.detail) {
            return String(responseData.detail);
        }

        if (responseData?.title) {
            return String(responseData.title);
        }

        return error?.message || String(error);
    }

    async function deriveVaultKey(seedBytes, passphrase, accountId) {
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

            return await crypto.subtle.deriveKey(
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
        } finally {
            seed.fill(0);
        }
    }

    async function createSession(sessionId, secret, horizonUrl, passphrase) {
        const StellarSdk = requireStellarSdk();
        const normalizedSecret = String(secret || '').trim();
        const normalizedPassphrase = String(passphrase || '');
        const normalizedHorizon = normalizeHorizonUrl(horizonUrl);

        if (!normalizedSecret) {
            throw new Error('Database secret is required.');
        }
        if (!normalizedPassphrase) {
            throw new Error('Network passphrase is required.');
        }

        let keypair;
        try {
            keypair = StellarSdk.Keypair.fromSecret(normalizedSecret);
        } catch (error) {
            throw new Error(`Invalid Stellar secret key: ${error?.message || error}`);
        }

        const rootAccountId = keypair.publicKey();
        const server = new StellarSdk.Horizon.Server(normalizedHorizon, {
            allowHttp: normalizedHorizon.toLowerCase().startsWith('http://')
        });

        try {
            await server.loadAccount(rootAccountId);
            const cryptoKey = await deriveVaultKey(keypair.rawSecretKey(), normalizedPassphrase, rootAccountId);

            stellarSessions.set(sessionId, {
                keypair,
                server,
                passphrase: normalizedPassphrase,
                rootAccountId,
                cryptoKey,
                aadPrefix: `stellar-vault|v1|${normalizedPassphrase}|${rootAccountId}|`
            });

            return { rootAccountId };
        } catch (error) {
            throw new Error(formatStellarError(error));
        }
    }

    function deleteSession(sessionId) {
        const session = stellarSessions.get(sessionId);
        if (session) {
            try {
                session.keypair.rawSecretKey().fill(0);
            } catch {
                // The keypair may already have been cleared.
            }
        }
        stellarSessions.delete(sessionId);
    }

    async function loadChain(sessionId, maxChainLength) {
        const StellarSdk = requireStellarSdk();
        const session = requireSession(sessionId);
        const limit = Math.max(1, Math.min(Number(maxChainLength) || 64, 256));
        const visited = new Set();
        const nodes = [];
        let current = session.rootAccountId;

        while (true) {
            if (visited.has(current)) {
                throw new Error('The vault account chain contains a cycle.');
            }
            if (nodes.length >= limit) {
                throw new Error(`The vault chain exceeds the safety limit of ${limit} accounts.`);
            }

            visited.add(current);
            const account = await session.server.loadAccount(current);
            const data = { ...(account.data_attr || {}) };
            let nextAccountId = null;

            if (Object.prototype.hasOwnProperty.call(data, 'sv:next')) {
                const rawPointer = sdkBufferFromBase64(session, data['sv:next']);
                if (rawPointer.length !== 32) {
                    throw new Error(`Account ${current} has an invalid sv:next pointer.`);
                }
                nextAccountId = StellarSdk.StrKey.encodeEd25519PublicKey(rawPointer);
            }

            nodes.push({
                accountId: account.account_id,
                position: nodes.length,
                subentryCount: Number(account.subentry_count || 0),
                data,
                nextAccountId
            });

            if (!nextAccountId) {
                return nodes;
            }

            current = nextAccountId;
        }
    }

    async function createAndAppendChild(
        sessionId,
        previousAccountId,
        position,
        childStartingBalance,
        baseFeeStroops) {
        const StellarSdk = requireStellarSdk();
        const session = requireSession(sessionId);
        const child = StellarSdk.Keypair.random();

        try {
            const rootAccount = await session.server.loadAccount(session.rootAccountId);
            const transaction = new StellarSdk.TransactionBuilder(rootAccount, {
                fee: String(baseFeeStroops),
                networkPassphrase: session.passphrase
            })
                .addOperation(StellarSdk.Operation.createAccount({
                    destination: child.publicKey(),
                    startingBalance: String(childStartingBalance)
                }))
                .addOperation(StellarSdk.Operation.setOptions({
                    source: child.publicKey(),
                    signer: {
                        ed25519PublicKey: session.rootAccountId,
                        weight: 1
                    },
                    masterWeight: 0,
                    lowThreshold: 1,
                    medThreshold: 1,
                    highThreshold: 1
                }))
                .addOperation(StellarSdk.Operation.manageData({
                    source: previousAccountId,
                    name: 'sv:next',
                    value: child.rawPublicKey()
                }))
                .setTimeout(60)
                .build();

            transaction.sign(session.keypair);
            transaction.sign(child);

            await session.server.submitTransaction(transaction);
            const childAccount = await session.server.loadAccount(child.publicKey());

            return {
                accountId: childAccount.account_id,
                position: Number(position),
                subentryCount: Number(childAccount.subentry_count || 0),
                data: { ...(childAccount.data_attr || {}) },
                nextAccountId: null
            };
        } catch (error) {
            throw new Error(formatStellarError(error));
        } finally {
            try {
                child.rawSecretKey().fill(0);
            } catch {
                // Best-effort cleanup.
            }
        }
    }

    async function submitManageData(sessionId, writes, baseFeeStroops) {
        const StellarSdk = requireStellarSdk();
        const session = requireSession(sessionId);

        if (!Array.isArray(writes) || writes.length === 0) {
            throw new Error('No ManageData writes were supplied.');
        }

        try {
            const rootAccount = await session.server.loadAccount(session.rootAccountId);
            let builder = new StellarSdk.TransactionBuilder(rootAccount, {
                fee: String(baseFeeStroops),
                networkPassphrase: session.passphrase
            });

            for (const write of writes) {
                const value = sdkBufferFromBase64(session, write.valueBase64);
                builder = builder.addOperation(StellarSdk.Operation.manageData({
                    source: write.sourceAccountId,
                    name: write.name,
                    value
                }));
            }

            const transaction = builder.setTimeout(60).build();
            transaction.sign(session.keypair);
            const response = await session.server.submitTransaction(transaction);
            return response.hash || '';
        } catch (error) {
            throw new Error(formatStellarError(error));
        }
    }

    async function encrypt(sessionId, recordId, plaintextBytes) {
        const session = requireSession(sessionId);
        const nonce = crypto.getRandomValues(new Uint8Array(12));
        const plaintext = new Uint8Array(plaintextBytes);
        const aad = encoder.encode(session.aadPrefix + recordId);

        const encryptedWithTag = new Uint8Array(await crypto.subtle.encrypt(
            {
                name: 'AES-GCM',
                iv: nonce,
                additionalData: aad,
                tagLength: 128
            },
            session.cryptoKey,
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

    async function decrypt(sessionId, recordId, envelopeBytes) {
        const session = requireSession(sessionId);
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
        const aad = encoder.encode(session.aadPrefix + recordId);

        try {
            return new Uint8Array(await crypto.subtle.decrypt(
                {
                    name: 'AES-GCM',
                    iv: nonce,
                    additionalData: aad,
                    tagLength: 128
                },
                session.cryptoKey,
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
        stellar: {
            createSession,
            deleteSession,
            loadChain,
            createAndAppendChild,
            submitManageData,
            encrypt,
            decrypt,
            sha256
        },
        crypto: {
            randomHex,
            generatePassword
        }
    };
})();
