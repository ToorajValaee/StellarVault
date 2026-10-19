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
    const rootRaw = await window.stellarVaultHd.accountSeed(seed, 0);
    let rootKeypair;
    try { rootKeypair = sdk.Keypair.fromRawEd25519Seed(rootRaw); }
    finally { rootRaw.fill(0); }
    const rootId = rootKeypair.publicKey();
    // Do not create or fund index 0. The user must provide an existing funded root.
    let root;
    try { root = await server.loadAccount(rootId); }
    catch (error) {
      if (Number(error?.response?.status) === 404)
        throw new Error('Mnemonic index-0 account does not exist on this network');
      throw error;
    }
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
    return { server, rootKeypair, rootAccountId: rootId, registry, networkPassphrase };
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
    const limit = Math.min(maxScan, highest + recoveryLookahead);
    if (highest === maxScan && recoveryLookahead > 0)
      throw new Error('Recovery scan cannot inspect beyond the registry index');
    if (highest > maxScan)
      throw new Error('Registry exceeds safe scan limit; raise bound explicitly');
    const nodes = [];
    for (let index = 1; index <= limit; index++) {
      const raw = await window.stellarVaultHd.accountSeed(seed, index);
      let pair;
      try { pair = window.StellarSdk.Keypair.fromRawEd25519Seed(raw); }
      finally { raw.fill(0); }
      try {
        const account = await opened.server.loadAccount(pair.publicKey());
        nodes.push({ index, accountId: pair.publicKey(),
          subentryCount: Number(account.subentry_count || 0),
          data: { ...(account.data_attr || {}) } });
      } catch (error) {
        // Missing indices are gaps, not a reason to stop scanning.
        if (Number(error?.response?.status) !== 404) throw error;
      }
    }
    const registeredGaps = [];
    for (let index = 1; index <= highest; index++) {
      if (!nodes.some(node => node.index === index)) registeredGaps.push(index);
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
