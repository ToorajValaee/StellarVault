// Read-only, bounded verification of an explicitly selected Stellar S-key vault.
// Never signs, submits, stores, or logs the secret key. This is a separate
// account-discovery mechanism; it must never fall back to HD derivation.
window.stellarVaultSecretChain = (() => {
  const LINK_NAMES = ['sv:next:v1', 'sv:next'];
  const MAX_CHAIN = 64;
  const validAddress = value =>
    typeof value === 'string' && window.StellarSdk?.StrKey?.isValidEd25519PublicKey(value);

  function decodeNext(account) {
    const data = account.data_attr || {};
    const present = LINK_NAMES.filter(name => Object.prototype.hasOwnProperty.call(data, name));
    if (present.length > 1) throw new Error('Conflicting chain pointers; investigation required.');
    if (!present.length) return null;
    let value;
    try { value = atob(data[present[0]]).trim(); }
    catch { throw new Error('Chain pointer is not valid base64.'); }
    if (!validAddress(value)) throw new Error('Chain pointer does not contain a valid Stellar account address.');
    return value;
  }

  function signerStatus(account, root) {
    const signer = (account.signers || []).find(x =>
      x.type === 'ed25519_public_key' && x.key === root);
    const weight = Number(signer?.weight || 0);
    const medium = Number(account.thresholds?.med_threshold);
    const high = Number(account.thresholds?.high_threshold);
    const low = Number(account.thresholds?.low_threshold);
    if (![weight, low, medium, high].every(Number.isInteger) ||
        low < 1 || medium < 1 || high < 1 || weight < 0 || weight > 255 ||
        low > 255 || medium > 255 || high > 255)
      throw new Error('Invalid Stellar signer or threshold state.');
    return {
      rootCanSign: weight > 0 && weight >= medium && weight >= high,
      weight, medium, high
    };
  }

  async function inspect(secretKey, horizonUrl, networkPassphrase) {
    const sdk = window.StellarSdk;
    if (!sdk?.Keypair || !sdk?.Horizon?.Server || !sdk?.StrKey)
      throw new Error('The bundled Stellar SDK is unavailable.');
    if (typeof secretKey !== 'string' || !sdk.StrKey.isValidEd25519SecretSeed(secretKey))
      throw new Error('Enter a valid Stellar S-key with a correct checksum.');
    const url = new URL(String(horizonUrl || '').trim());
    const loopback = ['localhost','127.0.0.1','[::1]'].includes(url.hostname);
    if ((url.protocol !== 'https:' && !(loopback && url.protocol === 'http:')) ||
        url.username || url.password || url.search || url.hash || url.pathname !== '/')
      throw new Error('Use an HTTPS Horizon origin without credentials, paths or query parameters.');
    if (!networkPassphrase || typeof networkPassphrase !== 'string')
      throw new Error('Select a Stellar network passphrase first.');
    // Authentication against the selected network requires comparing Horizon's
    // advertised passphrase. An incorrect endpoint must never be trusted.
    const response = await fetch(url.origin, { cache: 'no-store', headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error('Unable to verify the selected Stellar network.');
    const network = await response.json();
    if (network.network_passphrase !== networkPassphrase)
      throw new Error('Horizon network identity differs from the selected network.');

    const root = sdk.Keypair.fromSecret(secretKey).publicKey();
    const server = new sdk.Horizon.Server(url.origin, { allowHttp: loopback && url.protocol === 'http:' });
    const seen = new Set();
    const nodes = [];
    let current = root;
    for (let index = 0; index < MAX_CHAIN && current; index++) {
      if (seen.has(current)) throw new Error('A cycle was detected in the linked account chain.');
      seen.add(current);
      const account = await server.loadAccount(current);
      if (index === 0 && Object.prototype.hasOwnProperty.call(
          account.data_attr || {}, 'sv:registry:v2'))
        throw new Error('This account is registered as an HD-v2 vault. Open it with its mnemonic instead.');
      const signer = signerStatus(account, root);
      if (index > 0 && (account.signers || []).some(x =>
          x.key !== root && Number(x.weight || 0) > 0))
        throw new Error('Child account has an additional active signer. Single-secret recovery is unsafe.');
      if (!signer.rootCanSign || signer.medium < 1 || signer.high < 1)
        throw new Error('Account ' + index + ' lacks safe root-signing authorization or signer thresholds. Chain inspection stopped.');
      nodes.push({
        AccountId: current,
        Position: index,
        RootCanSign: true,
        RootSignerWeight: signer.weight,
        MediumThreshold: signer.medium,
        HighThreshold: signer.high,
        DataEntries: Object.keys(account.data_attr || {}).length
      });
      current = decodeNext(account);
      if (current && seen.has(current)) throw new Error('Cycle detected in chain pointers.');
    }
    if (current) throw new Error('The chain exceeds the safe discovery limit of 64 accounts.');
    return {
      RootAccountId: root,
      NetworkPassphrase: networkPassphrase,
      TailAccountId: nodes[nodes.length - 1].AccountId,
      Accounts: nodes,
      HasChain: nodes.length > 1,
      Note: 'Read-only signer and pointer inspection. No transaction or vault decryption performed.'
    };
  }
  return Object.freeze({ inspect });
})();
