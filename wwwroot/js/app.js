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

    async function inspectHorizon(horizonUrl) {
        const normalizedHorizon = normalizeHorizonUrl(horizonUrl);

        async function getJson(path) {
            const response = await fetch(`${normalizedHorizon}${path}`, {
                method: 'GET',
                headers: { Accept: 'application/json' },
                cache: 'no-store'
            });

            if (!response.ok) {
                throw new Error(`Horizon discovery failed (${response.status}) for ${path || '/'}.`);
            }

            return await response.json();
        }

        try {
            const [root, ledgers, feeStats] = await Promise.all([
                getJson('/'),
                getJson('/ledgers?order=desc&limit=1'),
                getJson('/fee_stats').catch(() => null)
            ]);

            const latestLedger = ledgers?._embedded?.records?.[0] || null;
            const networkPassphrase = String(root?.network_passphrase || '');
            const baseReserveStroops = Number(latestLedger?.base_reserve_in_stroops || 0);
            const minimumBaseFeeStroops = Number(
                feeStats?.last_ledger_base_fee ||
                latestLedger?.base_fee_in_stroops ||
                100);
            const recommendedBaseFeeStroops = Number(
                feeStats?.max_fee?.p50 ||
                feeStats?.fee_charged?.p50 ||
                minimumBaseFeeStroops);

            if (!networkPassphrase) {
                throw new Error('Horizon did not return network_passphrase from its root endpoint.');
            }
            if (!Number.isFinite(baseReserveStroops) || baseReserveStroops <= 0) {
                throw new Error('Horizon did not return a valid base reserve from its latest ledger.');
            }

            return {
                networkPassphrase,
                baseReserveStroops: Math.trunc(baseReserveStroops),
                minimumBaseFeeStroops: Math.max(1, Math.trunc(minimumBaseFeeStroops)),
                recommendedBaseFeeStroops: Math.max(1, Math.trunc(recommendedBaseFeeStroops)),
                horizonVersion: String(root?.horizon_version || '')
            };
        } catch (error) {
            throw new Error(formatStellarError(error));
        }
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

    // Explicit v2 entrypoint: never interpret a mnemonic as a Stellar secret.
    // This read-only path deliberately does not enable legacy account writes.
    async function openHdSession(sessionId, mnemonic, horizonUrl, networkPassphrase, bip39Passphrase = '') {
        if (stellarSessions.has(sessionId))
            throw new Error('Session identifier is already in use.');
        const seed = await window.stellarVaultHd.mnemonicSeed(mnemonic, bip39Passphrase);
        if (!window.stellarVaultHdDiscovery || !window.stellarVaultEncryption)
            throw new Error('V2 modules are unavailable.');
        try {
            const opened = await window.stellarVaultHdDiscovery.open(
                seed, normalizeHorizonUrl(horizonUrl), String(networkPassphrase));
            const cryptoKey = await window.stellarVaultEncryption.deriveKey(
                seed, opened.networkPassphrase, opened.rootAccountId);
            stellarSessions.set(sessionId, {
                ...opened, keypair: opened.rootKeypair,
                passphrase: opened.networkPassphrase, cryptoKey,
                hdSeed: seed.slice(), mode: 'hd-v2'
            });
            return { rootAccountId: opened.rootAccountId,
                registryInitialized: opened.registry !== null };
        } finally {
            seed.fill(0);
        }
    }

    async function initializeHdRegistry(sessionId, baseReserveStroops, baseFeeStroops,
        safetyBufferStroops = '0') {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2')
            throw new Error('An unlocked v2 session is required.');
        if (session.registry !== null)
            throw new Error('Registry already initialized; refusing to overwrite it.');
        if (!window.stellarVaultHdBootstrap)
            throw new Error('V2 registry bootstrap module is unavailable.');
        return window.stellarVaultHdBootstrap.initialize(session.hdSeed, session, {
            baseReserveStroops: String(baseReserveStroops),
            baseFeeStroops: String(baseFeeStroops),
            safetyBufferStroops: String(safetyBufferStroops)
        });
    }

    async function discoverHdNodes(sessionId, recoveryLookahead = 16) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2')
            throw new Error('Indexed discovery requires a v2 session.');
        return window.stellarVaultHdDiscovery.discover(session.hdSeed, session,
            { recoveryLookahead });
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
        if (session?.hdSeed) session.hdSeed.fill(0);
        stellarSessions.delete(sessionId);
    }

    async function loadChain(sessionId, maxChainLength) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2')
            throw new Error('Only v2 indexed sessions are supported.');
        if (!session.registry)
            throw new Error('Authenticated v2 registry is missing; initialize the existing root first.');
        const max = Number(maxChainLength);
        if (!Number.isSafeInteger(max) || max < 1)
            throw new Error('Invalid indexed account limit.');
        const highest = session.registry.highestAllocatedIndex;
        if (highest > max)
            throw new Error('Indexed registry exceeds configured account limit.');
        // Scan the registered range plus a recovery lookahead. Never follow sv:next.
        const discovered = await window.stellarVaultHdDiscovery.discover(
            session.hdSeed, session, { recoveryLookahead: 16 });
        if (discovered.requiresReconciliation)
            throw new Error('HD account gaps or unregistered children detected; reconcile before reading.');
        const root = await session.server.loadAccount(session.rootAccountId);
        return [{
            accountId: session.rootAccountId, position: 0,
            subentryCount: Number(root.subentry_count || 0),
            data: { ...(root.data_attr || {}) }, nextAccountId: null
        }, ...discovered.nodes.map(node => ({
            accountId: node.accountId, position: node.index,
            subentryCount: node.subentryCount, data: node.data, nextAccountId: null
        }))];
    }

    async function createAndAppendChild(
        sessionId, previousAccountId, position, childStartingBalance, baseFeeStroops,
        baseReserveStroops) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2')
            throw new Error('Legacy child creation is no longer supported.');
        if (!session.registry)
            throw new Error('Initialize the authenticated root registry first.');
        if (typeof previousAccountId !== 'string' || !previousAccountId)
            throw new Error('Previous account identity is required.');
        if (Number(position) !== session.registry.highestAllocatedIndex + 1)
            throw new Error('Stale child allocation index; refresh before retrying.');
        const expectedPreviousIndex = session.registry.highestAllocatedIndex;
        const previousRaw = await window.stellarVaultHd.accountSeed(
            session.hdSeed, expectedPreviousIndex, session.derivationScheme);
        let expectedPreviousAccount;
        try {
            expectedPreviousAccount = requireStellarSdk()
                .Keypair.fromRawEd25519Seed(previousRaw).publicKey();
        } finally {
            previousRaw.fill(0);
        }
        if (previousAccountId !== expectedPreviousAccount)
            throw new Error('Previous account does not match the registered HD tail.');
        if (String(childStartingBalance) !== '0')
            throw new Error('V2 children must be created with zero starting balance.');
        if (typeof window.stellarVaultSponsorship?.writeSponsoredData !== 'function')
            throw new Error('Sponsored record writer is unavailable; refusing allocation.');
        if (!baseReserveStroops || BigInt(String(baseReserveStroops)) <= 0n)
            throw new Error('Detected base reserve is required for sponsorship.');
        const result = await window.stellarVaultHdAllocator.allocate(
            session.hdSeed, session, {
                baseReserveStroops: String(baseReserveStroops),
                baseFeeStroops: String(baseFeeStroops)
            });
        const child = await session.server.loadAccount(result.accountId);
        return {
            accountId: result.accountId, position: result.index,
            subentryCount: Number(child.subentry_count || 0),
            data: { ...(child.data_attr || {}) }, nextAccountId: null
        };
    }

    async function submitManageData(sessionId, writes, baseFeeStroops) {
        const StellarSdk = requireStellarSdk();
        const session = requireSession(sessionId);
        if (session.mode === 'hd-v2')
            throw new Error('Legacy linked-chain operations are disabled for v2 sessions.');

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
                const isDelete = write.valueBase64 === null || write.valueBase64 === undefined;
                const value = isDelete
                    ? null
                    : sdkBufferFromBase64(session, write.valueBase64);

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

    // Each v2 data entry is owned and signed by its deterministic child.
    // The root pays every transaction fee and sponsors reserves for new entries.
    async function submitHdManageData(sessionId, writes, baseFeeStroops, baseReserveStroops) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2' || !session.registry)
            throw new Error('Authenticated v2 session required.');
        if (!Array.isArray(writes) || writes.length === 0)
            throw new Error('At least one v2 ManageData write is required.');
        const sdk = requireStellarSdk();
        const fee = BigInt(String(baseFeeStroops));
        const reserve = BigInt(String(baseReserveStroops));
        if (fee <= 0n || reserve <= 0n)
            throw new Error('Valid base fee and reserve are required.');
        // A second tab/device may have advanced the registry since unlock.
        // Reject stale writes rather than signing against an old allocation map.
        const latestRoot = await session.server.loadAccount(session.rootAccountId);
        const encodedRegistry = latestRoot.data_attr?.['sv:registry:v2'];
        if (!encodedRegistry)
            throw new Error('V2 registry missing from root.');
        const macKey = await window.stellarVaultRegistry.registryMacKey(
            session.hdSeed, session.passphrase, session.rootAccountId);
        const latestRegistry = await window.stellarVaultRegistry.decode(
            macKey, Uint8Array.from(atob(encodedRegistry), c => c.charCodeAt(0)));
        if (latestRegistry.revision !== session.registry.revision ||
            latestRegistry.highestAllocatedIndex !== session.registry.highestAllocatedIndex ||
            latestRegistry.activeWriteIndex !== session.registry.activeWriteIndex)
            throw new Error('HD registry changed since unlock; refresh before writing.');
        const seen = new Set();
        const staged = [];
        for (const write of writes) {
            const index = Number(write.nodeIndex);
            if (!Number.isSafeInteger(index) || index < 1 ||
                index > session.registry.highestAllocatedIndex)
                throw new Error('Write targets an unregistered HD child.');
            const name = String(write.name || '');
            if (!name || new TextEncoder().encode(name).length > 64 ||
                name === 'sv:registry:v2' || name === 'sv:next')
                throw new Error('Invalid or reserved v2 ManageData name.');
            const unique = index + ':' + name;
            if (seen.has(unique)) throw new Error('Duplicate data key in batch.');
            seen.add(unique);
            const raw = await window.stellarVaultHd.accountSeed(session.hdSeed, index);
            let child;
            try { child = sdk.Keypair.fromRawEd25519Seed(raw); }
            finally { raw.fill(0); }
            if (write.sourceAccountId && write.sourceAccountId !== child.publicKey())
                throw new Error('Data source does not match deterministic child.');
            const value = write.valueBase64 == null
                ? null : sdkBufferFromBase64(session, write.valueBase64);
            if (value && value.length > 64)
                throw new Error('Stellar ManageData values cannot exceed 64 bytes.');
            staged.push({ child, name, value });
        }
        // Submit one child write per transaction. The sponsorship operations
        // are atomic with the data write; a failed transaction creates no entry.
        let lastHash = '';
        for (const entry of staged) {
            lastHash = await window.stellarVaultSponsorship.writeSponsoredData({
                server: session.server,
                rootKeypair: session.rootKeypair,
                childKeypair: entry.child,
                networkPassphrase: session.passphrase,
                baseReserveStroops: reserve.toString(),
                baseFeeStroops: fee.toString(),
                name: entry.name,
                value: entry.value
            });
        }
        return lastHash;
    }

    async function inspectRootTreasury(sessionId, baseReserveStroops, additionalSponsoredUnits = 0, expectedFeeStroops = '0', safetyBufferStroops = '0') {
        const session = requireSession(sessionId);
        if (!window.stellarVaultTreasury) {
            throw new Error('Treasury accounting module unavailable.');
        }
        return window.stellarVaultTreasury.inspect(
            session.server, session.rootAccountId, String(baseReserveStroops), {
                additionalSponsoredUnits,
                expectedFeeStroops: String(expectedFeeStroops),
                safetyBufferStroops: String(safetyBufferStroops)
            });
    }

    async function encrypt(sessionId, recordId, plaintextBytes) {
        const session = requireSession(sessionId);
        if (session.mode === 'hd-v2')
            throw new Error('Legacy linked-chain operations are disabled for v2 sessions.');
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
        if (session.mode === 'hd-v2')
            throw new Error('Legacy linked-chain operations are disabled for v2 sessions.');
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

    // V2 records bind ciphertext to its exact deterministic storage location.
    async function encryptHdRecord(sessionId, nodeIndex, recordId, generation, plaintextBytes) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2') throw new Error('V2 session required.');
        const nonce = crypto.getRandomValues(new Uint8Array(12));
        const plaintext = new Uint8Array(plaintextBytes);
        const aad = window.stellarVaultEncryption.recordAad(
            session.passphrase, session.rootAccountId, nodeIndex, recordId, generation);
        try {
            const ciphertext = new Uint8Array(await crypto.subtle.encrypt({
                name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128
            }, session.cryptoKey, plaintext));
            const envelope = new Uint8Array(13 + ciphertext.length);
            envelope[0] = 2;
            envelope.set(nonce, 1);
            envelope.set(ciphertext, 13);
            ciphertext.fill(0);
            return envelope;
        } finally {
            plaintext.fill(0);
            nonce.fill(0);
        }
    }

    async function decryptHdRecord(sessionId, nodeIndex, recordId, generation, envelopeBytes) {
        const session = requireSession(sessionId);
        if (session.mode !== 'hd-v2') throw new Error('V2 session required.');
        const envelope = new Uint8Array(envelopeBytes);
        if (envelope.length < 29 || envelope[0] !== 2)
            throw new Error('Invalid v2 AES-GCM envelope.');
        const aad = window.stellarVaultEncryption.recordAad(
            session.passphrase, session.rootAccountId, nodeIndex, recordId, generation);
        return new Uint8Array(await crypto.subtle.decrypt({
            name: 'AES-GCM', iv: envelope.subarray(1, 13),
            additionalData: aad, tagLength: 128
        }, session.cryptoKey, envelope.subarray(13)));
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
        network: {
            inspectHorizon
        },
        stellar: {
            createSession,
            openHdSession,
            discoverHdNodes,
            initializeHdRegistry,
            deleteSession,
            loadChain,
            createAndAppendChild,
            submitManageData,
            submitHdManageData,
            inspectRootTreasury,
            encrypt,
            decrypt,
            encryptHdRecord,
            decryptHdRecord,
            sha256
        },
        crypto: {
            randomHex,
            generatePassword
        },
        viewport: {
            prepareModal: () => window.stellarVaultViewport?.prepareModal(),
            stabilize: () => window.stellarVaultViewport?.stabilize()
        }
    };
})();

// PWA installation UI. Chromium's native dialog is available only after
// beforeinstallprompt fires. On Android we still show the install panel early,
// then upgrade its INSTALL button to the native prompt as soon as Chrome makes
// it available. iOS keeps the manual Home Screen instructions.
(() => {
    let deferredInstallPrompt = window.__stellarVaultInstallPrompt ?? null;
    let manualStepsVisible = false;
    let dismissedForPage = false;
    const iosDismissKey = 'stellar-vault.ios-install-dismissed.v1';

    const isStandalone = () =>
        window.matchMedia('(display-mode: standalone)').matches ||
        window.navigator.standalone === true;

    const isIos = () =>
        /iphone|ipad|ipod/i.test(window.navigator.userAgent) ||
        (window.navigator.platform === 'MacIntel' && window.navigator.maxTouchPoints > 1);

    const isAndroid = () => /android/i.test(window.navigator.userAgent);

    function createBanner() {
        let banner = document.getElementById('pwa-install-banner');
        if (banner) {
            return banner;
        }

        banner = document.createElement('section');
        banner.id = 'pwa-install-banner';
        banner.className = 'pwa-install-banner';
        banner.hidden = true;
        banner.setAttribute('role', 'status');
        banner.setAttribute('aria-live', 'polite');
        banner.innerHTML = `
            <div class="pwa-install-copy">
                <span class="pwa-install-kicker">:: SYSTEM MESSAGE</span>
                <strong id="pwa-install-title">INSTALL STELLAR VAULT</strong>
                <small id="pwa-install-message">Run this terminal as an installed application.</small>
                <ol id="pwa-install-steps" class="pwa-install-steps" hidden></ol>
            </div>
            <div class="pwa-install-actions">
                <button id="pwa-install-action" class="pixel-button primary compact" type="button">INSTALL</button>
                <button id="pwa-install-dismiss" class="mini-button" type="button">LATER</button>
            </div>`;

        document.body.appendChild(banner);

        banner.querySelector('#pwa-install-action').addEventListener('click', async () => {
            deferredInstallPrompt = window.__stellarVaultInstallPrompt ?? deferredInstallPrompt;

            if (deferredInstallPrompt) {
                const promptEvent = deferredInstallPrompt;
                deferredInstallPrompt = null;
                window.__stellarVaultInstallPrompt = null;
                banner.hidden = true;

                try {
                    await promptEvent.prompt();
                    await promptEvent.userChoice;
                } catch (error) {
                    console.error('Native PWA install prompt failed:', error);
                    banner.hidden = false;
                }
                return;
            }

            // Chrome hasn't supplied its install event yet. A website cannot
            // manufacture the native dialog, so reveal the browser-menu path.
            manualStepsVisible = !manualStepsVisible;
            const steps = banner.querySelector('#pwa-install-steps');
            steps.hidden = !manualStepsVisible;
            banner.querySelector('#pwa-install-action').textContent = manualStepsVisible
                ? 'HIDE STEPS'
                : 'INSTALL';
        });

        banner.querySelector('#pwa-install-dismiss').addEventListener('click', () => {
            banner.hidden = true;
            dismissedForPage = true;

            if (isIos()) {
                sessionStorage.setItem(iosDismissKey, '1');
            }
        });

        return banner;
    }

    function setSteps(banner, items) {
        const steps = banner.querySelector('#pwa-install-steps');
        steps.replaceChildren(...items.map(text => {
            const item = document.createElement('li');
            item.textContent = text;
            return item;
        }));
        steps.hidden = true;
        manualStepsVisible = false;
    }

    function showAndroidInstallBanner() {
        if (isStandalone() || !isAndroid() || dismissedForPage) {
            return;
        }

        deferredInstallPrompt = window.__stellarVaultInstallPrompt ?? deferredInstallPrompt;
        const banner = createBanner();
        banner.querySelector('#pwa-install-title').textContent = 'INSTALL ON ANDROID';
        banner.querySelector('#pwa-install-message').textContent = deferredInstallPrompt
            ? 'Stellar Vault is ready to install as an Android app.'
            : 'Install Stellar Vault from Chrome. The native dialog will open when Chrome makes it available.';
        banner.querySelector('#pwa-install-action').textContent = 'INSTALL';
        banner.querySelector('#pwa-install-action').hidden = false;
        setSteps(banner, [
            'Tap INSTALL again after Chrome marks the app as ready.',
            'Or open the Chrome menu (⋮).',
            'Choose Install app or Add to Home screen.',
            'Confirm Install.'
        ]);
        banner.hidden = false;
    }

    function showIosInstallHelp() {
        if (isStandalone() || !isIos() || sessionStorage.getItem(iosDismissKey) === '1') {
            return;
        }

        const banner = createBanner();
        banner.querySelector('#pwa-install-title').textContent = 'INSTALL ON IPHONE / IPAD';
        banner.querySelector('#pwa-install-message').textContent = 'Use Safari to add this terminal to the Home Screen.';
        setSteps(banner, [
            'Open this page in Safari.',
            'Tap the Share button.',
            'Choose Add to Home Screen.',
            'Tap Add.'
        ]);
        banner.querySelector('#pwa-install-action').textContent = 'HOW TO INSTALL';
        banner.querySelector('#pwa-install-action').hidden = false;
        banner.hidden = false;
    }

    function captureInstallPrompt(event) {
        event?.preventDefault?.();
        deferredInstallPrompt = event ?? window.__stellarVaultInstallPrompt;

        if (deferredInstallPrompt) {
            window.__stellarVaultInstallPrompt = deferredInstallPrompt;
            showAndroidInstallBanner();
        }
    }

    // Fallback listener for pages that do not include the early bootstrap.
    window.addEventListener('beforeinstallprompt', captureInstallPrompt);

    // Primary path: index.html captures the one-shot event before app.js loads.
    window.addEventListener('stellarvault:install-ready', () => {
        captureInstallPrompt(window.__stellarVaultInstallPrompt);
    });

    window.addEventListener('appinstalled', () => {
        deferredInstallPrompt = null;
        window.__stellarVaultInstallPrompt = null;
        const banner = document.getElementById('pwa-install-banner');
        if (banner) {
            banner.hidden = true;
        }
    });

    window.addEventListener('stellarvault:installed', () => {
        deferredInstallPrompt = null;
        window.__stellarVaultInstallPrompt = null;
        const banner = document.getElementById('pwa-install-banner');
        if (banner) {
            banner.hidden = true;
        }
    });

    function initializeInstallUi() {
        if (isIos()) {
            window.setTimeout(showIosInstallHelp, 1000);
            return;
        }

        if (isAndroid()) {
            // Restore the visible Android install panel from the earlier build.
            // It appears even if Chrome delays beforeinstallprompt, and upgrades
            // automatically when the native event arrives.
            window.setTimeout(showAndroidInstallBanner, 1000);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initializeInstallUi, { once: true });
    } else {
        initializeInstallUi();
    }

    window.stellarPwaInstallState = () => ({
        standalone: isStandalone(),
        ios: isIos(),
        android: isAndroid(),
        eventSeen: window.__stellarVaultInstallEventSeen === true,
        promptAvailable: Boolean(window.__stellarVaultInstallPrompt || deferredInstallPrompt),
        serviceWorkerControlled: Boolean(navigator.serviceWorker?.controller),
        secureContext: window.isSecureContext,
        displayMode: window.matchMedia('(display-mode: standalone)').matches ? 'standalone' : 'browser'
    });
})();

// Keep iOS standalone layout stable during keyboard and modal transitions.
// Android deliberately uses the browser's normal document scrolling; applying
// iOS visualViewport restoration there can repeatedly snap the page to the top.
(() => {
    const root = document.documentElement;
    const isIos =
        /iphone|ipad|ipod/i.test(window.navigator.userAgent) ||
        (window.navigator.platform === 'MacIntel' && window.navigator.maxTouchPoints > 1);
    const isAndroid = /android/i.test(window.navigator.userAgent);
    const isStandalone =
        window.matchMedia('(display-mode: standalone)').matches ||
        window.navigator.standalone === true;

    root.classList.toggle('ios-device', isIos);
    root.classList.toggle('android-device', isAndroid);
    root.classList.toggle('standalone-app', isStandalone);

    function blurActiveControl() {
        const active = document.activeElement;
        if (active && active !== document.body && typeof active.blur === 'function') {
            active.blur();
        }
    }

    // Non-iOS browsers must not receive scroll restoration or visualViewport
    // height overrides. They only need the active input to be released.
    if (!isIos) {
        root.style.removeProperty('--app-height');
        window.stellarVaultViewport = {
            prepareModal: blurActiveControl,
            stabilize: blurActiveControl
        };
        return;
    }

    let stableScrollX = 0;
    let stableScrollY = 0;
    let viewportRestoreTimer = 0;

    function syncAppHeight() {
        const visualViewport = window.visualViewport;
        const scale = Number(visualViewport?.scale || 1);
        const visualHeight = Number(visualViewport?.height || 0);
        const layoutHeight = Number(window.innerHeight || root.clientHeight || 0);

        const height = scale === 1 && visualHeight > 0
            ? Math.max(visualHeight, layoutHeight)
            : layoutHeight;

        if (height > 0) {
            root.style.setProperty('--app-height', `${Math.round(height)}px`);
        }
    }

    function temporarilyResetIosScale() {
        if (!isStandalone) {
            return;
        }

        const viewport = document.querySelector('meta[name="viewport"]');
        if (!viewport) {
            return;
        }

        const original = viewport.getAttribute('content') ||
            'width=device-width, initial-scale=1.0, viewport-fit=cover';

        window.clearTimeout(viewportRestoreTimer);
        viewport.setAttribute(
            'content',
            'width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover');

        viewportRestoreTimer = window.setTimeout(() => {
            viewport.setAttribute('content', original);
        }, 450);
    }

    function restoreScrollAndSize() {
        window.scrollTo(stableScrollX, stableScrollY);
        syncAppHeight();
    }

    function settleViewport() {
        syncAppHeight();
        window.requestAnimationFrame(syncAppHeight);
        window.setTimeout(syncAppHeight, 80);
        window.setTimeout(syncAppHeight, 280);
        window.setTimeout(syncAppHeight, 700);
    }

    function prepareModal() {
        stableScrollX = window.scrollX || 0;
        stableScrollY = window.scrollY || 0;
        blurActiveControl();
        settleViewport();
    }

    function stabilize() {
        blurActiveControl();
        temporarilyResetIosScale();

        window.requestAnimationFrame(restoreScrollAndSize);
        window.setTimeout(restoreScrollAndSize, 60);
        window.setTimeout(restoreScrollAndSize, 180);
        window.setTimeout(restoreScrollAndSize, 420);
        window.setTimeout(restoreScrollAndSize, 800);
    }

    window.stellarVaultViewport = {
        prepareModal,
        stabilize
    };

    window.addEventListener('pageshow', settleViewport, { passive: true });
    window.addEventListener('resize', syncAppHeight, { passive: true });
    window.addEventListener('orientationchange', settleViewport, { passive: true });
    window.visualViewport?.addEventListener('resize', syncAppHeight, { passive: true });

    if (isStandalone) {
        window.addEventListener('focusout', () => window.setTimeout(stabilize, 120), { passive: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', settleViewport, { once: true });
    } else {
        settleViewport();
    }
})();
