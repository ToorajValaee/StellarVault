// SVault v2 registry integrity. Caller must persist and check the last
// accepted revision to detect rollback of an otherwise valid old envelope.
window.stellarVaultRegistry = (() => {
  const encoder = new TextEncoder();
  const MAGIC = 0x53;
  const VERSION = 2;
  const LENGTH = 50; // 18-byte header + 32-byte HMAC-SHA256
  async function registryMacKey(seed, network, registryAccount) {
    if (!(seed instanceof Uint8Array) || seed.length !== 64)
      throw new TypeError('Expected a 64-byte mnemonic seed');
    if (!network || !registryAccount) throw new Error('Missing registry identity');
    const base = await crypto.subtle.importKey('raw', seed, 'HKDF', false, ['deriveBits']);
    const salt = await crypto.subtle.digest('SHA-256',
      encoder.encode('svault-v2/registry|' + network + '|' + registryAccount));
    const bits = await crypto.subtle.deriveBits({
      name: 'HKDF', hash: 'SHA-256', salt,
      info: encoder.encode('svault-v2/registry/mac')
    }, base, 256);
    try {
      return await crypto.subtle.importKey('raw', bits, {
        name: 'HMAC', hash: 'SHA-256'
      }, false, ['sign', 'verify']);
    } finally { new Uint8Array(bits).fill(0); }
  }
  function validate(highest, active, revision) {
    if (!Number.isSafeInteger(highest) || highest < 0 || highest > 0x7ffffffe ||
        !Number.isSafeInteger(active) || active < 0 || active > highest ||
        !Number.isSafeInteger(revision) || revision < 0)
      throw new Error('Invalid registry values');
  }
  async function encode(key, highest, active, revision) {
    validate(highest, active, revision);
    const header = new Uint8Array(18);
    const view = new DataView(header.buffer);
    header[0] = MAGIC; header[1] = VERSION;
    view.setInt32(2, highest, false);
    view.setInt32(6, active, false);
    view.setBigInt64(10, BigInt(revision), false);
    const signature = new Uint8Array(await crypto.subtle.sign('HMAC', key, header));
    const envelope = new Uint8Array(LENGTH);
    envelope.set(header);
    envelope.set(signature, 18);
    return envelope;
  }
  async function decode(key, envelope) {
    const bytes = new Uint8Array(envelope);
    if (bytes.length !== LENGTH || bytes[0] !== MAGIC || bytes[1] !== VERSION)
      throw new Error('Unsupported registry envelope');
    if (!await crypto.subtle.verify('HMAC', key, bytes.subarray(18), bytes.subarray(0, 18)))
      throw new Error('Registry authentication failed');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const highest = view.getInt32(2, false);
    const active = view.getInt32(6, false);
    const rawRevision = view.getBigInt64(10, false);
    if (rawRevision > BigInt(Number.MAX_SAFE_INTEGER))
      throw new Error('Registry revision exceeds safe integer range');
    const revision = Number(rawRevision);
    validate(highest, active, revision);
    return { version: VERSION, highestAllocatedIndex: highest,
      activeWriteIndex: active, revision };
  }
  return Object.freeze({ registryMacKey, encode, decode });
})();
