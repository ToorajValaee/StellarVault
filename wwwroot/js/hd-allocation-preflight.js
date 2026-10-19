// SVault v2 allocation preflight. Never reuse an index when an account
// already exists beyond the authenticated registry.
window.stellarVaultAllocationPreflight = (() => {
  async function check(seed, opened, options = {}) {
    if (!opened?.registry) throw new Error('Authenticated registry required');
    const highest = opened.registry.highestAllocatedIndex;
    if (!Number.isSafeInteger(highest) || highest < 0 || highest >= 4096)
      throw new Error('HD child allocation limit reached or registry index invalid');
    const discovery = await window.stellarVaultHdDiscovery.discover(
      seed, opened, { recoveryLookahead: options.recoveryLookahead ?? 16,
        maxScan: options.maxScan ?? 4096 });
    if (discovery.scannedThrough < opened.registry.highestAllocatedIndex + 1)
      throw new Error('Next allocation index was not scanned');
    if (discovery.requiresReconciliation)
      throw new Error('Derived account registry mismatch detected; reconcile before allocation');
    const nextIndex = opened.registry.highestAllocatedIndex + 1;
    if (nextIndex > discovery.scannedThrough)
      throw new Error('Next index was not scanned; increase recovery lookahead');
    const latestRoot = await opened.server.loadAccount(opened.rootAccountId);
    const encoded = latestRoot.data_attr?.['sv:registry:v2'];
    if (!encoded) throw new Error('Root registry missing during allocation');
    const macKey = await window.stellarVaultRegistry.registryMacKey(
      seed, opened.networkPassphrase, opened.rootAccountId);
    const latest = await window.stellarVaultRegistry.decode(macKey,
      Uint8Array.from(atob(encoded), c => c.charCodeAt(0)));
    if (latest.revision !== opened.registry.revision ||
        latest.highestAllocatedIndex !== opened.registry.highestAllocatedIndex ||
        latest.activeWriteIndex !== opened.registry.activeWriteIndex)
      throw new Error('Registry changed during allocation; refresh required');
    const raw = await window.stellarVaultHd.accountSeed(seed, nextIndex);
    try {
      const keypair = window.StellarSdk.Keypair.fromRawEd25519Seed(raw);
      return { nextIndex, accountId: keypair.publicKey(),
        registryRevision: opened.registry.revision };
    } finally { raw.fill(0); }
  }
  return Object.freeze({ check });
})();
