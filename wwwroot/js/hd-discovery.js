// Read-only deterministic account discovery for SVault v2.
// Accepts a 64-byte BIP39 seed from a separately checksum-validated mnemonic.
// Does not create, fund, or mutate accounts.
window.stellarVaultHdDiscovery = (() => {
  const REGISTRY_KEY = 'sv:registry:v2';
  const MAX_SCAN = 4096;

  async function open(seed, horizonUrl, networkPassphrase) {
    if (!(seed instanceof Uint8Array) || seed.length !== 64)
      throw new Error('Expected a validated BIP39 64-byte seed');
    const sdk = window.StellarSdk;
    if (!sdk?.Horizon?.Server || !window.stellarVaultHd ||
        !window.stellarVaultRegistry || !window.stellarVaultTreasury)
      throw new Error('HD modules or Stellar SDK unavailable');
    const url = String(horizonUrl || '').trim().replace(/\/+$/, '');
    if (!url || !networkPassphrase) throw new Error('Network settings required');
    const server = new sdk.Horizon.Server(url, { allowHttp: url.startsWith('http://') });
    // Prefer standard SEP-0005; try legacy BIP32 only when the standard
    // account is definitively absent. Never mask network or auth failures.
    let root, rootKeypair, rootId, derivationScheme;
    const absent = [];
    for (const scheme of ['sep5', 'bip32-compat']) {
      const rootRaw = await window.stellarVaultHd.accountSeed(seed, 0, scheme);
      let candidate;
      try { candidate = sdk.Keypair.fromRawEd25519Seed(rootRaw); }
      finally { rootRaw.fill(0); }
      const accountId = candidate.publicKey();
      try {
        root = await server.loadAccount(accountId);
        rootKeypair = candidate;
        rootId = accountId;
        derivationScheme = scheme;
        break;
      } catch (error) {
        if (Number(error?.response?.status) !== 404) throw error;
        absent.push(accountId);
      }
    }
    if (!root)
      throw new Error('No index-0 account found for this mnemonic on the selected network. Checked standard Stellar derivation (' + absent[0] + ') and compatible derivation (' + absent[1] + '). Verify your mnemonic, optional BIP39 passphrase and network. Do not fund an unverified address.');
    const native = (root.balances || []).find(b => b.asset_type === 'native');
    if (!native) throw new Error('Root account has no native XLM balance');
    const macKey = await window.stellarVaultRegistry.registryMacKey(
      seed, networkPassphrase, rootId);
    const registryEncoded = root.data_attr?.[REGISTRY_KEY];
    let registry = null;
    if (registryEncoded) {
      const bytes = Uint8Array.from(atob(registryEncoded), c => c.charCodeAt(0));
      registry = await window.stellarVaultRegistry.decode(macKey, bytes);
    }
    return { server, rootKeypair, rootAccountId: rootId, registry, networkPassphrase, derivationScheme };
  }

  async function discover(seed, opened, { recoveryLookahead = 16, maxScan = MAX_SCAN } = {}) {
    if (!(seed instanceof Uint8Array) || seed.length !== 64)
      throw new Error('Expected a validated BIP39 64-byte seed');
    if (!opened?.server || !opened.rootAccountId)
      throw new Error('Open the existing root account first');
    if (!Number.isInteger(recoveryLookahead) || recoveryLookahead < 0 ||
        !Number.isInteger(maxScan) || maxScan < 1 || maxScan > MAX_SCAN)
      throw new RangeError('Invalid recovery scan bounds');
    const highest = opened.registry?.highestAllocatedIndex ?? 0;
    if (!Number.isSafeInteger(highest) || highest < 0)
      throw new Error('Registry contains an invalid highest allocated index');
    const limit = Math.min(maxScan, highest + recoveryLookahead);
    if (highest > 0 && recoveryLookahead === 0)
      throw new Error('Recovery lookahead must inspect at least the next index');
    if (highest > maxScan)
      throw new Error('Registry exceeds safe scan limit; raise bound explicitly');
    const nodes = [];
    // Bound Horizon concurrency while preserving deterministic index order.
    // Bootstrap scans 4096 paths; sequential network calls are prohibitively slow.
    const batchSize = 12;
    async function inspectIndex(index) {
      const raw = await window.stellarVaultHd.accountSeed(seed, index, opened.derivationScheme || 'sep5');
      let pair;
      try { pair = window.StellarSdk.Keypair.fromRawEd25519Seed(raw); }
      finally { raw.fill(0); }
      try {
        const account = await opened.server.loadAccount(pair.publicKey());
        return { index, accountId: pair.publicKey(),
          subentryCount: Number(account.subentry_count || 0),
          data: { ...(account.data_attr || {}) } };
      } catch (error) {
        if (Number(error?.response?.status) === 404) return null;
        throw error;
      }
    }
    for (let start = 1; start <= limit; start += batchSize) {
      const end = Math.min(limit, start + batchSize - 1);
      const indices = Array.from({ length: end - start + 1 },
        (_, offset) => start + offset);
      const batch = await Promise.all(indices.map(inspectIndex));
      for (const node of batch) if (node !== null) nodes.push(node);
    }
    const discovered = new Set(nodes.map(node => node.index));
    const registeredGaps = [];
    for (let index = 1; index <= highest; index++) {
      if (!discovered.has(index)) registeredGaps.push(index);
    }
    const unregisteredNodes = nodes.filter(node => node.index > highest);
    return { nodes, scannedThrough: limit, registry: opened.registry,
      registeredGaps, unregisteredNodes,
      requiresReconciliation: unregisteredNodes.length > 0 || registeredGaps.length > 0 };
  }

  async function rootTreasury(opened, baseReserveStroops, options = {}) {
    return window.stellarVaultTreasury.inspect(
      opened.server, opened.rootAccountId, baseReserveStroops, options);
  }

  return Object.freeze({ open, discover, rootTreasury, REGISTRY_KEY });
})();
