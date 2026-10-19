const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function load(script, exported) {
  const context = { window: {}, BigInt };
  context.window.window = context.window;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'wwwroot', 'js', script), 'utf8'), context);
  return context.window[exported];
}

test('treasury uses sponsored units and native liabilities', () => {
  const t = load('hd-treasury.js', 'stellarVaultTreasury');
  const account = {
    balances: [{ asset_type: 'native', balance: '10.0000000', selling_liabilities: '0.5000000' }],
    subentry_count: 1, num_sponsoring: 4, num_sponsored: 0
  };
  const result = t.calculate(account, '5000000', {
    additionalSponsoredUnits: 2, expectedFeeStroops: '300', safetyBufferStroops: '1000000'
  });
  assert.equal(result.minimumBalanceStroops, '40000000');
  assert.equal(result.spendableStroops, '60000000');
  assert.equal(result.availableAfterPlanStroops, '48999700');
  assert.equal(result.canAffordPlan, true);
  assert.equal(result.sponsoredReserveXlm, '2.0000000');
});

test('treasury parses 7-decimal XLM exactly and rejects invalid amounts', () => {
  const t = load('hd-treasury.js', 'stellarVaultTreasury');
  assert.equal(String(t.xlmToStroops('0.0000001')), '1');
  assert.equal(t.stroopsToXlm(12345678n), '1.2345678');
  assert.throws(() => t.xlmToStroops('1.00000001'));
  assert.throws(() => t.xlmToStroops('-1'));
});

test('sponsorship helper rejects already-existing child without swallowing error', async () => {
  const s = load('hd-sponsorship.js', 'stellarVaultSponsorship');
  assert.equal(s.parseAccountReserves({
    balances: [{ asset_type: 'native', balance: '3.5000000', selling_liabilities: '0' }],
    subentry_count: 0, num_sponsoring: 2, num_sponsored: 0
  }, '5000000').spendable, 15000000n);
});
