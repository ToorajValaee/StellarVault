// Read-only live root treasury health. All monetary amounts are integer stroops.
// Never infer available XLM from total native balance alone.
window.stellarVaultTreasury = (() => {
  const STROOPS = 10000000n;

  function xlmToStroops(value) {
    const input = String(value);
    if (!/^(0|[1-9][0-9]*)(\.[0-9]{1,7})?$/.test(input))
      throw new Error('Invalid Horizon XLM amount');
    const [whole, fraction = ''] = input.split('.');
    return BigInt(whole) * STROOPS + BigInt(fraction.padEnd(7, '0'));
  }

  function stroopsToXlm(value) {
    const n = BigInt(value);
    const sign = n < 0n ? '-' : '';
    const abs = n < 0n ? -n : n;
    return sign + String(abs / STROOPS) + '.' +
      String(abs % STROOPS).padStart(7, '0');
  }

  function calculate(account, baseReserveStroops, options = {}) {
    const reserve = BigInt(String(baseReserveStroops));
    if (reserve <= 0n) throw new Error('Invalid base reserve');
    const native = (account.balances || []).find(b => b.asset_type === 'native');
    if (!native) throw new Error('Horizon account has no native balance');
    const subentries = BigInt(account.subentry_count ?? 0);
    const sponsoring = BigInt(account.num_sponsoring ?? 0);
    const sponsored = BigInt(account.num_sponsored ?? 0);
    const additionalUnits = BigInt(options.additionalSponsoredUnits ?? 0);
    const fee = BigInt(options.expectedFeeStroops ?? 0);
    const buffer = BigInt(options.safetyBufferStroops ?? 0);
    if ([subentries, sponsoring, sponsored, additionalUnits, fee, buffer]
      .some(n => n < 0n)) throw new Error('Negative treasury input');

    const units = 2n + subentries + sponsoring - sponsored;
    if (units < 0n) throw new Error('Invalid root sponsorship counts');
    const balance = xlmToStroops(native.balance);
    const liabilities = xlmToStroops(native.selling_liabilities ?? '0');
    const minimum = units * reserve + liabilities;
    const spendable = balance > minimum ? balance - minimum : 0n;
    const projected = additionalUnits * reserve + fee + buffer;
    return {
      balanceStroops: String(balance),
      minimumBalanceStroops: String(minimum),
      spendableStroops: String(spendable),
      availableAfterPlanStroops: String(spendable > projected ? spendable - projected : 0n),
      canAffordPlan: spendable >= projected,
      sponsoredReserveUnits: String(sponsoring),
      sponsoredReserveXlm: stroopsToXlm(sponsoring * reserve),
      balanceXlm: stroopsToXlm(balance),
      spendableXlm: stroopsToXlm(spendable)
    };
  }

  async function inspect(server, rootAccountId, baseReserveStroops, options = {}) {
    const account = await server.loadAccount(rootAccountId);
    return calculate(account, baseReserveStroops, options);
  }

  return Object.freeze({ calculate, inspect, xlmToStroops, stroopsToXlm });
})();
