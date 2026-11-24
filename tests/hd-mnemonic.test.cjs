const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');

function load() {
  const context = vm.createContext({ window: {}, crypto: webcrypto, TextEncoder,
    Uint8Array, DataView });
  const root = path.join(__dirname, '../src/StellarVault.Pwa/wwwroot/js');
  for (const file of ['bip39-english.js', 'hd-keys.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  return context.window.stellarVaultHd;
}

test('mnemonic errors never echo an entered word', async () => {
  const hd = load();
  const enteredWord = 'synthetic-sensitive-input';
  await assert.rejects(hd.validateMnemonic(`${'abandon '.repeat(11)}${enteredWord}`), error => {
    assert.match(error.message, /unknown BIP-39 English word/);
    assert.ok(!error.message.includes(enteredWord));
    return true;
  });
});

test('BIP-39 validation accepts canonical checksum and rejects changed checksum', async () => {
  const hd = load();
  const phrase = `${'abandon '.repeat(11)}about`;
  assert.equal(await hd.validateMnemonic(phrase), phrase);
  await assert.rejects(hd.validateMnemonic('abandon '.repeat(12).trim()), /checksum mismatch/);
});
