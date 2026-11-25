const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { URL } = require('node:url');
const { TextEncoder, TextDecoder } = require('node:util');
const fs = require('node:fs');
const path = require('node:path');

function create() {
  const data = new Map();
  const storage = {
    getItem: key => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
    removeItem: key => data.delete(key)
  };
  const sessionData = new Map();
  const sessionStorage = {
    getItem: key => sessionData.get(key) ?? null,
    setItem: (key, value) => sessionData.set(key, value),
    removeItem: key => sessionData.delete(key)
  };
  const ctx = vm.createContext({window:{},localStorage:storage,sessionStorage,URL,TextEncoder,TextDecoder});
  const script = fs.readFileSync(path.join(__dirname,'../src/StellarVault.Pwa/wwwroot/js/vault-v2-recent-vault.js'),'utf8');
  vm.runInContext(script,ctx);
  return {api:ctx.window.stellarVaultRecentVault,data,sessionData};
}
const account = 'G' + 'A'.repeat(55);
test('remembers only public account and network',()=>{
  const {api,data}=create();
  api.save(account,'testnet');
  const raw=[...data.values()][0];
  assert.deepEqual(Object.keys(JSON.parse(raw)).sort(),['account','horizonUrl','network','networkPassphrase','version']);
  assert.equal(api.load().account,account);
});
test('custom network saves endpoint but no network passphrase or credentials',()=>{
  const {api,data}=create();
  api.save(account,'custom','https://horizon.example.com/','Custom Stellar Network');
  const raw=[...data.values()][0];
  assert.deepEqual(Object.keys(JSON.parse(raw)).sort(),['account','horizonUrl','network','networkPassphrase','version']);
  assert.equal(api.load().network,'custom');
  assert.equal(api.load().horizonUrl,'https://horizon.example.com/');
  assert.equal(api.load().networkPassphrase,'Custom Stellar Network');
});
test('custom network refuses untrusted URL forms',()=>{
  const {api,data}=create();
  assert.throws(()=>api.save(account,'custom','https://user:pass@example.com/','Custom Stellar Network'));
  assert.throws(()=>api.save(account,'custom','http://example.com/','Custom Stellar Network'));
  assert.throws(()=>api.save(account,'custom','https://example.com/path','Custom Stellar Network'));
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

test('custom network requires a public network identity to support offline reopen',()=>{
  const {api,data}=create();
  assert.throws(()=>api.save(account,'custom','https://horizon.example.com/'));
  assert.equal(data.size,0);
});

test('active hybrid resume is tab scoped and not implied by saved encrypted credentials', () => {
  const {api,data,sessionData} = create();
  api.save(account,'testnet');
  assert.equal(api.hasHybridSession(account,'testnet'),false,
    'Remembered device vault must not force passphrase-only access');
  api.markHybridSession(account,'testnet');
  assert.equal(api.hasHybridSession(account,'testnet'),true);
  assert.equal(api.hasHybridSession(account,'public'),false);
  assert.equal(api.hasHybridSession('G'+'B'.repeat(55),'testnet'),false);
  assert.equal(data.size,1,'hybrid resume must not put anything in localStorage');
  assert.equal(sessionData.size,1);
  api.clearHybridSession();
  assert.equal(api.hasHybridSession(account,'testnet'),false,
    'End session must prevent a saved vault from hijacking a later on-chain login');
  assert.equal(data.size,1,'End session must not silently delete encrypted user data');
});
