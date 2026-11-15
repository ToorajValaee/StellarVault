// Zero-XLM deterministic child provisioning through Stellar reserve sponsorship.
// Root funds reserves (not child balances) and is the transaction fee payer.
// Call only after deriving and validating the child keypair from BIP39/SLIP10.
window.stellarVaultSponsorship = (() => {
  function requireSdk() {
    if (!window.StellarSdk) throw new Error('Stellar SDK unavailable');
    return window.StellarSdk;
  }

  function parseAccountReserves(account, baseReserveStroops) {
    const reserve = BigInt(String(baseReserveStroops));
    const units = 2n +
      BigInt(account.subentry_count || 0) +
      BigInt(account.num_sponsoring || 0) -
      BigInt(account.num_sponsored || 0);
    const native = (account.balances || []).find(b => b.asset_type === 'native');
    if (!native || reserve <= 0n || units < 0n)
      throw new Error('Invalid Horizon root balance or reserve information');
    function toStroops(value) {
      const str = String(value || '0');
      if (!/^\d+(\.\d{1,7})?$/.test(str))
        throw new Error('Invalid native-asset amount');
      const [whole, fractional = ''] = str.split('.');
      return BigInt(whole) * 10000000n + BigInt(fractional.padEnd(7, '0'));
    }
    const liabilities = toStroops(native.selling_liabilities);
    const balance = toStroops(native.balance);
    const minimum = units * reserve + liabilities;
    return { balance, minimum, spendable: balance > minimum ? balance - minimum : 0n };
  }

  async function createSponsoredChild({
    server, rootKeypair, childKeypair, networkPassphrase,
    baseReserveStroops, baseFeeStroops, safetyBufferStroops = '0'
  }) {
    const sdk = requireSdk();
    if (rootKeypair.publicKey() === childKeypair.publicKey())
      throw new Error('Root and child accounts must differ');
    const root = await server.loadAccount(rootKeypair.publicKey());
    let childExists = false;
    try {
      await server.loadAccount(childKeypair.publicKey());
      childExists = true;
    } catch (error) {
      if (Number(error?.response?.status) !== 404) throw error;
    }
    if (childExists)
      throw new Error('Derived child account already exists; reconcile registry before proceeding');

    // Account creation consumes two reserve units sponsored by root.
    const fee = BigInt(String(baseFeeStroops));
    const required = 2n * BigInt(String(baseReserveStroops)) +
      3n * fee + BigInt(String(safetyBufferStroops));
    if (fee <= 0n || BigInt(String(baseReserveStroops)) <= 0n ||
        BigInt(String(safetyBufferStroops)) < 0n)
      throw new Error('Invalid sponsorship fee, reserve or safety buffer');
    const funds = parseAccountReserves(root, baseReserveStroops);
    if (funds.spendable < required)
      throw new Error('Root account has insufficient spendable native asset for sponsorship and fees');

    const transaction = new sdk.TransactionBuilder(root, {
      fee: fee.toString(), networkPassphrase
    })
      .addOperation(sdk.Operation.beginSponsoringFutureReserves({
        sponsoredId: childKeypair.publicKey(),
        source: rootKeypair.publicKey()
      }))
      .addOperation(sdk.Operation.createAccount({
        destination: childKeypair.publicKey(),
        startingBalance: '0',
        source: rootKeypair.publicKey()
      }))
      .addOperation(sdk.Operation.endSponsoringFutureReserves({
        source: childKeypair.publicKey()
      }))
      .setTimeout(60).build();

    transaction.sign(rootKeypair);
    transaction.sign(childKeypair);
    const result = await server.submitTransaction(transaction);
    return { accountId: childKeypair.publicKey(), transactionHash: result.hash };
  }

  async function writeSponsoredData({
    server, rootKeypair, childKeypair, networkPassphrase,
    baseReserveStroops, baseFeeStroops, name, value,
    safetyBufferStroops = '0', onSignedTransaction = null,
    manifestGuards = null, expectedValueBase64 = null,
    requireAbsentTarget = false
  }) {
    const sdk = requireSdk();
    if (rootKeypair.publicKey() === childKeypair.publicKey())
      throw new Error('Root and storage child must be different accounts');
    // Root-first sequence fence:
    // Every HD-v2 sponsored mutation is a transaction whose source account is
    // this root. Capture its sequence BEFORE reading any mutable child data.
    // If another StellarVault device commits after this read, its transaction
    // advances the root sequence and this transaction is rejected as stale.
    // If it committed before this read, the child/guard reads below observe
    // the post-commit ledger state and the manifest byte checks reject stale
    // intents. Parallel root/child reads do not provide this ordering.
    const root = await server.loadAccount(rootKeypair.publicKey());
    if (root.accountId() !== rootKeypair.publicKey())
      throw new Error('Horizon returned a different root account');
    const child = await server.loadAccount(childKeypair.publicKey());
    // Optimistic defense in depth at the last signing boundary. The guard
    // compares actual Horizon ManageData bytes, not a caller-supplied claim
    // about decrypted content. This does not make later ledger writes atomic.
    if (manifestGuards !== null || expectedValueBase64 !== null || requireAbsentTarget) {
      if (!name.startsWith('svh:'))
        throw new Error('Manifest guards cannot apply to non-manifest writes');
      if (manifestGuards !== null) {
        if (!Array.isArray(manifestGuards) || manifestGuards.length < 1 ||
            manifestGuards.length > 63)
          throw new Error('Invalid manifest guard inventory');
        const seenOwners = new Set();
        for (const guard of manifestGuards) {
          if (!guard || guard.name !== name ||
              typeof guard.sourceAccountId !== 'string' ||
              !/^G[A-Z2-7]{55}$/.test(guard.sourceAccountId) ||
              typeof guard.expectedValueBase64 !== 'string' ||
              guard.expectedValueBase64.length > 88 ||
              seenOwners.has(guard.sourceAccountId))
            throw new Error('Ambiguous manifest guard');
          seenOwners.add(guard.sourceAccountId);
          const account = guard.sourceAccountId === childKeypair.publicKey()
            ? child : await server.loadAccount(guard.sourceAccountId);
          if (account.data_attr?.[name] !== guard.expectedValueBase64)
            throw new Error('An HD manifest changed since the last guarded read; no transaction was signed');
        }
      }
      if (expectedValueBase64 !== null &&
          child.data_attr?.[name] !== expectedValueBase64)
        throw new Error('The deletion target manifest changed; no transaction was signed');
      if (requireAbsentTarget &&
          Object.prototype.hasOwnProperty.call(child.data_attr || {}, name))
        throw new Error('The new manifest target has become occupied; no transaction was signed');
    }
    const exists = Object.prototype.hasOwnProperty.call(child.data_attr || {}, name);
    if (value === null && !exists)
      throw new Error('Data entry was already removed; refresh before deleting');
    const createsEntry = value !== null && !exists;
    const fee = BigInt(String(baseFeeStroops));
    const operationCount = createsEntry ? 3n : 1n;
    const required = (createsEntry ? BigInt(String(baseReserveStroops)) : 0n) +
      operationCount * fee + BigInt(String(safetyBufferStroops));
    if (fee <= 0n || BigInt(String(baseReserveStroops)) <= 0n ||
        BigInt(String(safetyBufferStroops)) < 0n)
      throw new Error('Invalid sponsorship fee, reserve or safety buffer');
    if (parseAccountReserves(root, baseReserveStroops).spendable < required)
      throw new Error('Root account has insufficient spendable native asset for data operation');

    let builder = new sdk.TransactionBuilder(root, {
      fee: fee.toString(), networkPassphrase
    });
    if (createsEntry) {
      builder = builder.addOperation(sdk.Operation.beginSponsoringFutureReserves({
        sponsoredId: childKeypair.publicKey(), source: rootKeypair.publicKey()
      }));
    }
    builder = builder.addOperation(sdk.Operation.manageData({
      source: childKeypair.publicKey(), name, value
    }));
    if (createsEntry) {
      builder = builder.addOperation(sdk.Operation.endSponsoringFutureReserves({
        source: childKeypair.publicKey()
      }));
    }
    const transaction = builder.setTimeout(60).build();
    transaction.sign(rootKeypair);
    transaction.sign(childKeypair);
    // A hybrid create's final manifest hash must reach encrypted durable
    // storage BEFORE broadcast. The callback is awaited, and any persistence
    // failure prevents the final transaction from being sent.
    if (onSignedTransaction !== null) {
      if (typeof onSignedTransaction !== 'function')
        throw new Error('Invalid signed transaction checkpoint handler');
      const signedHash = transaction.hash().toString('hex');
      if (!/^[0-9a-f]{64}$/.test(signedHash))
        throw new Error('Invalid signed transaction identity');
      await onSignedTransaction(signedHash);
    }
    const response = await server.submitTransaction(transaction);
    if (onSignedTransaction !== null && response.hash &&
        String(response.hash).toLowerCase() !== transaction.hash().toString('hex'))
      throw new Error('Horizon receipt differs from checkpointed signed manifest identity');
    return response.hash;
  }

  return Object.freeze({ parseAccountReserves, createSponsoredChild, writeSponsoredData });
})();
