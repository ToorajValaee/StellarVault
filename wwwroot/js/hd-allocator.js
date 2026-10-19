// Deterministic v2 child allocation. Requires a previously authenticated registry.
// Child creation and registry persistence are separate ledger transactions; a failed
// registry update leaves an orphan that discovery must reconcile before retrying.
window.stellarVaultHdAllocator = (() => {
  async function allocate(seed, opened, options) {
    if (!opened?.registry || !opened?.rootKeypair || !opened?.server)
      throw new Error('Open an authenticated root registry before allocating');
    if (!options?.baseReserveStroops || !options?.baseFeeStroops)
      throw new Error('Reserve and fee settings are required');
    const preflight = await window.stellarVaultAllocationPreflight.check(seed, opened);
    const childRaw = await window.stellarVaultHd.accountSeed(seed, preflight.nextIndex);
    let child;
    try { child = window.StellarSdk.Keypair.fromRawEd25519Seed(childRaw); }
    finally { childRaw.fill(0); }
    if (child.publicKey() !== preflight.accountId)
      throw new Error('Derived child changed after allocation preflight');
    // Reserve for the child account (2 units) AND root registry entry if absent.
    const root = await opened.server.loadAccount(opened.rootAccountId);
    const registryExists = Object.prototype.hasOwnProperty.call(
      root.data_attr || {}, 'sv:registry:v2');
    const reserveUnits = registryExists ? 2 : 3;
    const treasury = window.stellarVaultTreasury.calculate(root,
      options.baseReserveStroops, {
        additionalSponsoredUnits: reserveUnits,
        expectedFeeStroops: String(BigInt(String(options.baseFeeStroops)) * 4n),
        safetyBufferStroops: options.safetyBufferStroops ?? '0'
      });
    if (!treasury.canAffordPlan)
      throw new Error('Root treasury cannot cover sponsored child and registry update');
    const created = await window.stellarVaultSponsorship.createSponsoredChild({
      server: opened.server, rootKeypair: opened.rootKeypair, childKeypair: child,
      networkPassphrase: opened.networkPassphrase,
      baseReserveStroops: options.baseReserveStroops,
      baseFeeStroops: options.baseFeeStroops,
      safetyBufferStroops: options.safetyBufferStroops ?? '0'
    });
    // Re-check the registry after creation to avoid overwriting a newer revision.
    const latestRoot = await opened.server.loadAccount(opened.rootAccountId);
    const encoded = latestRoot.data_attr?.['sv:registry:v2'];
    if (!encoded)
      throw new Error('Child created but root registry missing; reconcile before retrying');
    const key = await window.stellarVaultRegistry.registryMacKey(
      seed, opened.networkPassphrase, opened.rootAccountId);
    const latest = await window.stellarVaultRegistry.decode(key,
      Uint8Array.from(atob(encoded), c => c.charCodeAt(0)));
    if (latest.revision !== preflight.registryRevision ||
        latest.highestAllocatedIndex !== opened.registry.highestAllocatedIndex)
      throw new Error('Child created but registry changed; reconcile before retrying');
    const nextRegistry = await window.stellarVaultRegistry.encode(key,
      preflight.nextIndex, preflight.nextIndex, latest.revision + 1);
    const sdk = window.StellarSdk;
    const tx = new sdk.TransactionBuilder(latestRoot, {
      fee: String(options.baseFeeStroops),
      networkPassphrase: opened.networkPassphrase
    }).addOperation(sdk.Operation.manageData({
      name: 'sv:registry:v2', value: nextRegistry,
      source: opened.rootAccountId
    })).setTimeout(60).build();
    tx.sign(opened.rootKeypair);
    let committed;
    try { committed = await opened.server.submitTransaction(tx); }
    catch (error) {
      throw new Error('Child created but registry commit failed; reconcile before retrying: ' +
        (error?.message || String(error)));
    }
    opened.registry = {
      version: 2, highestAllocatedIndex: preflight.nextIndex,
      activeWriteIndex: preflight.nextIndex, revision: latest.revision + 1
    };
    return {
      index: preflight.nextIndex, accountId: child.publicKey(),
      creationHash: created.transactionHash, registryHash: committed.hash,
      registry: opened.registry
    };
  }
  return Object.freeze({ allocate });
})();
