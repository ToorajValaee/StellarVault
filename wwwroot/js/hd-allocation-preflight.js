// SVault v2 allocation preflight. Never reuse an index when an account
// already exists beyond the authenticated registry.
window.stellarVaultAllocationPreflight = (() => {
  async function check(seed, opened, options = {}) {
    if (!opened?.registry) throw new Error('Authenticated registry required');
    const discovery = await window.stellarVaultHdDiscovery.discover(
      seed, opened, { recoveryLookahead: options.recoveryLookahead ?? 16,
        maxScan: options.maxScan ?? 4096 });
    if (discovery.scannedThrough < opened.registry.highestAllocatedIndex + 1)
      throw new Error('Next allocation index was not scanned');
    if (discovery.requiresReconciliation)
      throw new Error('Unregistered derived accounts detected; reconcile before allocation');
    const nextIndex = opened.registry.highestAllocatedIndex + 1;
    if (nextIndex > discovery.scannedThrough)
      throw new Error('Next index was not scanned; increase recovery lookahead');
    const raw = await window.stellarVaultHd.accountSeed(seed, nextIndex);
    try {
      const keypair = window.StellarSdk.Keypair.fromRawEd25519Seed(raw);
      return { nextIndex, accountId: keypair.publicKey(),
        registryRevision: opened.registry.revision };
    } finally { raw.fill(0); }
  }
  return Object.freeze({ check });
})();
