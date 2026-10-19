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
        throw new Error('Invalid native XLM amount');
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
    if (fee <= 0n || required < 0n)
      throw new Error('Invalid sponsorship fee or reserve');
    const funds = parseAccountReserves(root, baseReserveStroops);
    if (funds.spendable < required)
      throw new Error('Root account has insufficient spendable XLM for sponsorship and fees');

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
    safetyBufferStroops = '0'
  }) {
    const sdk = requireSdk();
    const [root, child] = await Promise.all([
      server.loadAccount(rootKeypair.publicKey()),
      server.loadAccount(childKeypair.publicKey())
    ]);
    const exists = Object.prototype.hasOwnProperty.call(child.data_attr || {}, name);
    const createsEntry = value !== null && !exists;
    const fee = BigInt(String(baseFeeStroops));
    const operationCount = createsEntry ? 3n : 1n;
    const required = (createsEntry ? BigInt(String(baseReserveStroops)) : 0n) +
      operationCount * fee + BigInt(String(safetyBufferStroops));
    if (fee <= 0n || required < 0n)
      throw new Error('Invalid sponsorship fee or reserve');
    if (parseAccountReserves(root, baseReserveStroops).spendable < required)
      throw new Error('Root account has insufficient spendable XLM for data operation');

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
    const response = await server.submitTransaction(transaction);
    return response.hash;
  }

  return Object.freeze({ parseAccountReserves, createSponsoredChild, writeSponsoredData });
})();
