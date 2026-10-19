// Deterministic v2 child allocation. Requires a previously authenticated registry.
// Child creation and registry persistence share one atomic Stellar transaction.
// Concurrent devices must refresh after any conflicting ledger submission.
window.stellarVaultHdAllocator = (() => {
  const pendingRoots = new Set();
  async function allocate(seed, opened, options) {
    const rootId = opened?.rootAccountId;
    if (!rootId) throw new Error('Missing root account identity');
    if (pendingRoots.has(rootId))
      throw new Error('Child allocation already in progress for this root');
    pendingRoots.add(rootId);
    try {
      return await allocateOnce(seed, opened, options);
    } finally {
      pendingRoots.delete(rootId);
    }
  }
  async function allocateOnce(seed, opened, options) {
    if (!opened?.registry || !opened?.rootKeypair || !opened?.server)
      throw new Error('Open an authenticated root registry before allocating');
    if (!options?.baseReserveStroops || !options?.baseFeeStroops)
      throw new Error('Reserve and fee settings are required');
    const reserve = BigInt(String(options.baseReserveStroops));
    const fee = BigInt(String(options.baseFeeStroops));
    const buffer = BigInt(String(options.safetyBufferStroops ?? '0'));
    if (reserve <= 0n || fee <= 0n || buffer < 0n)
      throw new Error('Invalid reserve, fee or safety buffer');
    const preflight = await window.stellarVaultAllocationPreflight.check(seed, opened);
    const childRaw = await window.stellarVaultHd.accountSeed(seed, preflight.nextIndex);
    let child;
    try { child = window.StellarSdk.Keypair.fromRawEd25519Seed(childRaw); }
    finally { childRaw.fill(0); }
    if (child.publicKey() !== preflight.accountId)
      throw new Error('Derived child changed after allocation preflight');
    // Re-check the registry before the single atomic ledger transaction.
    const latestRoot = await opened.server.loadAccount(opened.rootAccountId);
    const encoded = latestRoot.data_attr?.['sv:registry:v2'];
    if (!encoded)
      throw new Error('Authenticated root registry missing; initialize it first');
    const key = await window.stellarVaultRegistry.registryMacKey(
      seed, opened.networkPassphrase, opened.rootAccountId);
    const latest = await window.stellarVaultRegistry.decode(key,
      Uint8Array.from(atob(encoded), c => c.charCodeAt(0)));
    if (latest.revision !== preflight.registryRevision ||
        latest.highestAllocatedIndex !== opened.registry.highestAllocatedIndex ||
        latest.activeWriteIndex !== opened.registry.activeWriteIndex)
      throw new Error('Registry changed before child allocation; refresh and retry');
    // Use the same fresh account snapshot for both treasury validation and
    // transaction sequence; never authorize against an earlier balance.
    const treasury = window.stellarVaultTreasury.calculate(latestRoot,
      options.baseReserveStroops, {
        additionalSponsoredUnits: 2,
        expectedFeeStroops: String(fee * 4n),
        safetyBufferStroops: buffer.toString()
      });
    if (!treasury.canAffordPlan)
      throw new Error('Root treasury cannot cover sponsored child and registry update');
    const nextRegistry = await window.stellarVaultRegistry.encode(key,
      preflight.nextIndex, preflight.nextIndex, latest.revision + 1);
    const sdk = window.StellarSdk;
    const tx = new sdk.TransactionBuilder(latestRoot, {
      fee: String(options.baseFeeStroops),
      networkPassphrase: opened.networkPassphrase
    }).addOperation(sdk.Operation.beginSponsoringFutureReserves({
      source: opened.rootAccountId, sponsoredId: child.publicKey()
    })).addOperation(sdk.Operation.createAccount({
      source: opened.rootAccountId, destination: child.publicKey(), startingBalance: '0'
    })).addOperation(sdk.Operation.endSponsoringFutureReserves({
      source: child.publicKey()
    })).addOperation(sdk.Operation.manageData({
      name: 'sv:registry:v2', value: nextRegistry,
      source: opened.rootAccountId
    })).setTimeout(60).build();
    tx.sign(opened.rootKeypair);
    tx.sign(child);
    let committed;
    try { committed = await opened.server.submitTransaction(tx); }
    catch (error) {
      throw new Error('Atomic child allocation transaction failed; refresh before retrying: ' +
        (error?.message || String(error)));
    }
    opened.registry = {
      version: 2, highestAllocatedIndex: preflight.nextIndex,
      activeWriteIndex: preflight.nextIndex, revision: latest.revision + 1
    };
    return {
      index: preflight.nextIndex, accountId: child.publicKey(),
      creationHash: committed.hash, registryHash: committed.hash,
      registry: opened.registry
    };
  }
  return Object.freeze({ allocate });
})();
