// Transport recovery metadata only. This is NOT queued record confirmation.
// No credentials, plaintext records, automatic resubmission or guessed hashes.
window.stellarVaultTransactionRecovery = (() => {
  const sessions = new Map(), encoder = new TextEncoder(), decoder = new TextDecoder();
  const fail = message => { throw new Error(message); };
  const hex = bytes => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  const hash = async value => new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(value)));
  function database() {
    const db = window.stellarVaultV2Db;
    if (!db?.readQueue || !db?.replaceQueue || !db?.deleteQueueIfMatch)
      fail('Encrypted transaction recovery storage is unavailable; no broadcast attempted.');
    return db;
  }
  async function attach(token, strategy, root, passphrase, horizon, signer, server) {
    if (!['hd', 'skey'].includes(strategy) || !/^G[A-Z2-7]{55}$/.test(root) || !passphrase)
      fail('Invalid transaction recovery scope.');
    const endpoint = new URL(horizon);
    if ((endpoint.protocol !== 'https:' && !(endpoint.protocol === 'http:' &&
        ['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname))) ||
        endpoint.username || endpoint.password || endpoint.pathname !== '/' || endpoint.search || endpoint.hash)
      fail('Invalid recovery Horizon endpoint.');
    const scope = await hash(JSON.stringify(['transport-v1', strategy, root, passphrase]));
    const seed = new Uint8Array(signer.rawSecretKey());
    let key;
    try {
      const material = await crypto.subtle.importKey('raw', seed, 'HKDF', false, ['deriveKey']);
      key = await crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: scope,
        info: encoder.encode('StellarVault transaction recovery v1') }, material,
        { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    } finally { seed.fill(0); }
    const state = { token, strategy, root, passphrase, horizon: endpoint.origin,
      storageKey: 'transport-v1:' + hex(scope) + ':pending', key, consent: false };
    sessions.set(token, state);
    const transport = server.submitTransaction.bind(server);
    server.submitTransaction = transaction => submit(state, transaction, transport);
  }
  function selected(strategy, root, passphrase) {
    const matches = [...sessions.values()].filter(s => s.strategy === strategy &&
      s.root === root && s.passphrase === passphrase);
    if (matches.length !== 1) fail('Reconnect one signing session before reviewing recovery.');
    return matches[0];
  }
  function current(s) {
    if (sessions.get(s.token) !== s) fail('Signing session was revoked; no new broadcast permitted.');
  }
  async function seal(s, value) {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const plain = encoder.encode(JSON.stringify(value));
    try {
      const cipher = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv,
        additionalData: encoder.encode(s.storageKey) }, s.key, plain));
      const result = new Uint8Array(13 + cipher.length);
      result[0] = 1; result.set(iv, 1); result.set(cipher, 13);
      return result;
    } finally { plain.fill(0); }
  }
  async function unseal(s, bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 30 || bytes.length > 524288 || bytes[0] !== 1)
      fail('Recovery journal is malformed; writes remain blocked.');
    let plain;
    try {
      plain = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: bytes.slice(1, 13),
        additionalData: encoder.encode(s.storageKey) }, s.key, bytes.slice(13)));
      const value = JSON.parse(decoder.decode(plain));
      if (value.version !== 1 || value.root !== s.root || value.horizon !== s.horizon ||
          value.networkId !== hex(await hash(s.passphrase)) || !/^[0-9a-f]{64}$/.test(value.hash) ||
          typeof value.envelope !== 'string' || value.envelope.length > 262144 ||
          !Number.isSafeInteger(value.createdAt) || value.createdAt <= 0)
        fail('Recovery journal scope or transaction is invalid; writes remain blocked.');
      return value;
    } catch { fail('Recovery journal could not be authenticated; writes remain blocked.'); }
    finally { if (plain) plain.fill(0); }
  }
  async function receipt(s, value, allowFailed = false) {
    // A trusted-endpoint receipt observation, never an independent record proof.
    const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 10000);
    try {
      async function readJson(url, limit) {
      const response = await fetch(url, { headers: { Accept: 'application/json' },
        credentials: 'omit', redirect: 'error', cache: 'no-store', signal: controller.signal });
      if (!response.ok) fail('The signed transaction or network is not proven; recovery remains blocked.');
      const reader = response.body.getReader();
      let total = 0; const parts = [];
      try {
        while (true) {
          const { value: part, done } = await reader.read();
          if (done) break;
          total += part.length;
          if (total > limit) fail('Recovery response exceeds the inspection limit.');
          parts.push(part);
        }
      } finally { await reader.cancel(); }
      const bytes = new Uint8Array(total); let offset = 0;
      for (const part of parts) { bytes.set(part, offset); offset += part.length; }
      return JSON.parse(decoder.decode(bytes));
      }
      const network = await readJson(s.horizon + '/', 65536);
      if (network.network_passphrase !== s.passphrase)
        fail('Recovery endpoint reports a different network; writes remain blocked.');
      const found = await readJson(s.horizon + '/transactions/' + value.hash, 1048576);
      if (found.hash !== value.hash || (found.successful !== true && !(allowFailed && found.successful === false)) ||
          !Number.isSafeInteger(found.ledger) || found.ledger <= 0 || found.envelope_xdr !== value.envelope)
        fail('Receipt does not exactly match the signed transaction; recovery remains blocked.');
      return { Ledger: found.ledger, Successful: found.successful };
    } finally { clearTimeout(timer); }
  }
  async function release(s, bytes, value, allowFailed = false) {
    const observation = await receipt(s, value, allowFailed);
    current(s);
    if (!await database().deleteQueueIfMatch(s.storageKey, bytes))
      fail('Recovery state changed during verification; writes remain blocked.');
    return observation;
  }
  async function submit(s, transaction, transport) {
    current(s);
    await assertWritable(s.token);
    const db = database();
    if (await db.readQueue(s.storageKey)) fail('An earlier signed transaction needs recovery. Do not retry it.');
    const signedHash = hex(new Uint8Array(transaction.hash()));
    const envelope = transaction.toXDR();
    if (!/^[0-9a-f]{64}$/.test(signedHash) || typeof envelope !== 'string' || envelope.length > 262144)
      fail('Signed transaction cannot be safely checkpointed; no broadcast attempted.');
    const value = { version: 1, root: s.root, networkId: hex(await hash(s.passphrase)),
      horizon: s.horizon, hash: signedHash, envelope, createdAt: Date.now() };
    const bytes = await seal(s, value);
    if (!await db.replaceQueue(s.storageKey, null, bytes))
      fail('Another tab reserved this vault transaction slot; no broadcast attempted.');
    current(s);
    if (!s.consent) fail('Recovery consent was revoked; reserved transaction remains quarantined.');
    const result = await transport(transaction); // Exactly one attempt. Never retry.
    if (result?.hash?.toLowerCase() !== signedHash || result.successful === false)
      fail('Submission response is ambiguous; keep the recovery journal and do not retry.');
    await release(s, bytes, value);
    return result;
  }
  async function assertWritable(token) {
    const s = sessions.get(token);
    if (!s) fail('Reconnect the signer before reviewing a write.');
    current(s);
    if (!s.consent) fail('Allow the encrypted transaction-recovery journal before signing writes. No broadcast attempted.');
    if (await database().readQueue(s.storageKey))
      fail('An earlier signed transaction needs recovery. Do not retry it.');
  }
  async function inspect(strategy, root, passphrase) {
    const s = selected(strategy, root, passphrase);
    const bytes = await database().readQueue(s.storageKey);
    const value = bytes ? await unseal(s, bytes) : null;
    return { Consent: s.consent, PendingHash: value?.hash ?? null };
  }
  function consent(strategy, root, passphrase, allowed) {
    const s = selected(strategy, root, passphrase);
    s.consent = allowed === true;
  }
  async function verify(strategy, root, passphrase) {
    const s = selected(strategy, root, passphrase);
    const bytes = await database().readQueue(s.storageKey);
    if (!bytes) fail('No durable transaction checkpoint exists. This cannot clear another quarantine.');
    return release(s, bytes, await unseal(s, bytes), true);
  }
  async function inspectSession(strategy) {
    const matches = [...sessions.values()].filter(s => s.strategy === strategy);
    if (matches.length !== 1) fail('Reconnect one signing session before reviewing recovery.');
    const s = matches[0];
    return { ...await inspect(strategy, s.root, s.passphrase), Token: s.token, RootAccountId: s.root };
  }
  function sessionConsent(token, allowed) {
    const s = sessions.get(token);
    if (!s) fail('Signing session changed; review recovery consent again.');
    consent(s.strategy, s.root, s.passphrase, allowed);
  }
  function verifySession(token) {
    const s = sessions.get(token);
    if (!s) fail('Signing session changed; reconnect before read-only verification.');
    return verify(s.strategy, s.root, s.passphrase);
  }
  return Object.freeze({ attach, assertWritable, inspect, consent, verify, inspectSession, sessionConsent, verifySession,
    detach: token => sessions.delete(token) });
})();
