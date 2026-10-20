// Initialize the v2 authenticated registry on an EXISTING funded index-0 root.
// No account creation, no legacy linked-list migration, and no silent overwrite.
window.stellarVaultHdBootstrap = (() => {
  async function initialize(seed, opened, { baseReserveStroops, baseFeeStroops,
    safetyBufferStroops = '0' } = {}) {
    if (!(seed instanceof Uint8Array) || seed.length !== 64)
      throw new TypeError('Expected a validated BIP39 seed');
    if (!opened?.rootKeypair || !opened?.server || !opened?.rootAccountId ||
        !opened?.networkPassphrase)
      throw new Error('Existing funded index-0 root must be opened first');
    if (opened.rootKeypair.publicKey() !== opened.rootAccountId)
      throw new Error('Root signer does not match the existing root account');
    if (opened.registry !== null)
      throw new Error('Registry already loaded; initialization cannot overwrite it');
    const reserve = BigInt(String(baseReserveStroops));
    const fee = BigInt(String(baseFeeStroops));
    const buffer = BigInt(String(safetyBufferStroops));
    if (reserve <= 0n || fee <= 0n || buffer < 0n)
      throw new Error('Invalid registry initialization reserve or fee');
    const discovery = await window.stellarVaultHdDiscovery.discover(seed, opened,
      { recoveryLookahead: 4096, maxScan: 4096 });
    if (discovery.nodes.length !== 0)
      throw new Error('Derived children already exist; recover registry instead of initializing');
    const root = await opened.server.loadAccount(opened.rootAccountId);
    if (Object.prototype.hasOwnProperty.call(root.data_attr || {}, 'sv:registry:v2'))
      throw new Error('Registry already exists on ledger; reopen and authenticate it');
    const treasury = window.stellarVaultTreasury.calculate(root, baseReserveStroops, {
      additionalSponsoredUnits: 1, expectedFeeStroops: fee.toString(),
      safetyBufferStroops: buffer.toString()
    });
    if (!treasury.canAffordPlan)
      throw new Error('Root treasury cannot afford registry entry and transaction fee');
    const key = await window.stellarVaultRegistry.registryMacKey(
      seed, opened.networkPassphrase, opened.rootAccountId);
    const value = await window.stellarVaultRegistry.encode(key, 0, 0, 0);
    const sdk = window.StellarSdk;
    const tx = new sdk.TransactionBuilder(root, {
      fee: fee.toString(), networkPassphrase: opened.networkPassphrase
    }).addOperation(sdk.Operation.manageData({
      source: opened.rootAccountId, name: 'sv:registry:v2', value: opened.rootKeypair.rawPublicKey().constructor.from(value)
    })).setTimeout(60).build();
    tx.sign(opened.rootKeypair);
    // A Horizon timeout can occur after the transaction reaches consensus.
    // Never retry initialization blindly: first check the authenticated ledger
    // state, and fail closed if it cannot be inspected.
    let result;
    let submissionError;
    try {
      result = await opened.server.submitTransaction(tx);
    } catch (error) {
      submissionError = error;
    }
    let verifiedRoot;
    try {
      verifiedRoot = await opened.server.loadAccount(opened.rootAccountId);
    } catch (error) {
      if (submissionError)
        throw new Error('Registry submission outcome is unknown and ledger verification failed; reopen the vault before retrying', { cause: submissionError });
      throw error;
    }
    const encoded = verifiedRoot.data_attr?.['sv:registry:v2'];
    if (!encoded) {
      if (submissionError)
        throw new Error('Registry submission did not produce a visible registry; refresh ledger state before retrying', { cause: submissionError });
      throw new Error('Registry transaction submitted but ledger entry is missing; refresh');
    }
    // Verify both authentication and exact intended payload; an unrelated
    // registry write must not be mistaken for successful initialization.
    const verifiedRegistry = await window.stellarVaultRegistry.decode(key,
      Uint8Array.from(atob(encoded), c => c.charCodeAt(0)));
    if (verifiedRegistry.highestAllocatedIndex !== 0 ||
        verifiedRegistry.activeWriteIndex !== 0 || verifiedRegistry.revision !== 0)
      throw new Error('Ledger registry differs from initialization payload; reopen and reconcile');
    opened.registry = verifiedRegistry;
    return { rootAccountId: opened.rootAccountId,
      transactionHash: result?.hash ?? null, registry: verifiedRegistry,
      recoveredAfterSubmissionError: Boolean(submissionError) };
  }
  return Object.freeze({ initialize });
})();
