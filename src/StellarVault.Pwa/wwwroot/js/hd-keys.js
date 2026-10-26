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
  async function validateMnemonic(mnemonic) {
    const words = String(mnemonic || '').normalize('NFKD').trim().split(/\s+/u);
    if (![12, 15, 18, 21, 24].includes(words.length))
      throw new Error('BIP-39 mnemonic must contain 12, 15, 18, 21, or 24 words');
    const wordlist = window.stellarVaultBip39English;
    if (!Array.isArray(wordlist) || wordlist.length !== 2048)
      throw new Error('Canonical BIP-39 English wordlist is unavailable');
    const positions = new Map(wordlist.map((word, index) => [word, index]));
    const indices = words.map(word => {
      const index = positions.get(word);
      if (index === undefined) throw new Error('Unknown BIP-39 English word: ' + word);
      return index;
    });
    const bitLength = indices.length * 11;
    const entropyBits = bitLength * 32 / 33;
    const checksumBits = bitLength - entropyBits;
    const bits = indices.map(index => index.toString(2).padStart(11, '0')).join('');
    const entropy = new Uint8Array(entropyBits / 8);
    for (let i = 0; i < entropy.length; i++)
      entropy[i] = parseInt(bits.slice(i * 8, i * 8 + 8), 2);
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', entropy));
    const expected = digest[0].toString(2).padStart(8, '0').slice(0, checksumBits);
    entropy.fill(0);
    if (bits.slice(entropyBits) !== expected)
      throw new Error('BIP-39 mnemonic checksum mismatch');
    return words.join(' ');
  }
  async function mnemonicSeed(validatedMnemonic, passphrase = '') {
    const phrase = await validateMnemonic(validatedMnemonic);
    const password = text.encode(phrase);
    const salt = text.encode('mnemonic' + String(passphrase).normalize('NFKD'));
    try {
      const key = await crypto.subtle.importKey('raw', password, 'PBKDF2', false, ['deriveBits']);
      return new Uint8Array(await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-512', salt, iterations: 2048 }, key, 512));
    } finally { password.fill(0); salt.fill(0); }
  }
  async function accountSeed(bip39Seed, index, scheme = 'sep5') {
    if (!(bip39Seed instanceof Uint8Array) || bip39Seed.length !== 64) throw new TypeError('Expected 64-byte BIP-39 seed');
    if (scheme === 'sep5') {
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
    throw new Error('Unsupported HD derivation scheme');
  }

  async function deriveKeypair(validatedMnemonic, index, passphrase = '', scheme = 'sep5') {
    if (!window.StellarSdk?.Keypair?.fromRawEd25519Seed) throw new Error('Stellar SDK unavailable');
    const seed = await mnemonicSeed(validatedMnemonic, passphrase);
    try {
      const raw = await accountSeed(seed, index, scheme);
      try { return window.StellarSdk.Keypair.fromRawEd25519Seed(raw); }
      finally { raw.fill(0); }
    } finally { seed.fill(0); }
  }
  return Object.freeze({ validateMnemonic, mnemonicSeed, accountSeed, deriveKeypair, path: (index) => { harden(index); return `m/44'/148'/${index}'`; } });
})();
