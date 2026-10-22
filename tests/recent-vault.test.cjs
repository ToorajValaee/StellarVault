const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

function create() {
  const data = new Map();
  const storage = {
    getItem: key => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
    removeItem: key => data.delete(key)
  };
  const ctx = vm.createContext({window:{},localStorage:storage});
  const script = fs.readFileSync(path.join(__dirname,'../src/StellarVault.Pwa/wwwroot/js/vault-v2-recent-vault.js'),'utf8');
  vm.runInContext(script,ctx);
  return {api:ctx.window.stellarVaultRecentVault,data};
}
const account = 'G' + 'A'.repeat(55);
test('remembers only public account and network',()=>{
  const {api,data}=create();
  api.save(account,'testnet');
  const raw=[...data.values()][0];
  assert.deepEqual(Object.keys(JSON.parse(raw)).sort(),['account','network','version']);
  assert.equal(api.load().account,account);
});
test('custom networks cannot be silently restored',()=>{
  const {api,data}=create();
  api.save(account,'custom');
  assert.equal(data.size,0);
});
test('invalid or corrupted selection is ignored',()=>{
  const {api,data}=create();
  data.set('stellar-vault-v2-recent-account','{');
  assert.equal(api.load(),null);
});
test('forget clears remembered account only',()=>{
  const {api,data}=create();
  api.save(account,'public');
  api.clear();
  assert.equal(data.size,0);
  assert.equal(api.load(),null);
});
