// HD derivation primitives for SVault v2. Requires a separately validated BIP-39 mnemonic.
window.stellarVaultHd = (() => {
  const text = new TextEncoder();
  const harden = (n) => {
    if (!Number.isSafeInteger(n) || n < 0 || n > 0x7fffffff) throw new RangeError('Invalid derivation index');
    return (n + 0x80000000) >>> 0;
  };
  async function hmac(key, data) {
    const imported = await crypto.subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-512' }, false, ['sign']);
    return new Uint8Array(await crypto.subtle.sign('HMAC', imported, data));
  }
  async function mnemonicSeed(validatedMnemonic, passphrase = '') {
    const phrase = String(validatedMnemonic).normalize('NFKD');
    const words = phrase.trim().split(/\s+/u);
    if (![12, 15, 18, 21, 24].includes(words.length)) throw new Error('Invalid mnemonic length');
    // Callers MUST verify BIP-39 wordlist and checksum before this function.
    const password = text.encode(words.join(' '));
    const salt = text.encode('mnemonic' + String(passphrase).normalize('NFKD'));
    try {
      const key = await crypto.subtle.importKey('raw', password, 'PBKDF2', false, ['deriveBits']);
      return new Uint8Array(await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-512', salt, iterations: 2048 }, key, 512));
    } finally { password.fill(0); salt.fill(0); }
  }
  async function accountSeed(bip39Seed, index) {
    if (!(bip39Seed instanceof Uint8Array) || bip39Seed.length !== 64) throw new TypeError('Expected 64-byte BIP-39 seed');
    let state = await hmac(text.encode('ed25519 seed'), bip39Seed);
    try {
      for (const segment of [44, 148, index]) {
        const data = new Uint8Array(37);
        data.set(state.subarray(0, 32), 1);
        new DataView(data.buffer).setUint32(33, harden(segment), false);
        const next = await hmac(state.subarray(32, 64), data);
        data.fill(0); state.fill(0); state = next;
      }
      return state.slice(0, 32);
    } finally { state.fill(0); }
  }
  async function deriveKeypair(validatedMnemonic, index, passphrase = '') {
    if (!window.StellarSdk?.Keypair?.fromRawEd25519Seed) throw new Error('Stellar SDK unavailable');
    const seed = await mnemonicSeed(validatedMnemonic, passphrase);
    try {
      const raw = await accountSeed(seed, index);
      try { return window.StellarSdk.Keypair.fromRawEd25519Seed(raw); }
      finally { raw.fill(0); }
    } finally { seed.fill(0); }
  }
  return Object.freeze({ mnemonicSeed, accountSeed, deriveKeypair, path: (index) => { harden(index); return `m/44'/148'/${index}'`; } });
})();
