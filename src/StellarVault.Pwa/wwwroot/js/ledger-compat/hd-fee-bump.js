// Stellar fee-bump support for SVault v2.
// The inner transaction MUST already contain all signatures required by its
// operations. The outer root signature only authorizes paying the fee.
window.stellarVaultFeeBump = (() => {
  function validateFee(maxFeeStroops) {
    const fee = BigInt(String(maxFeeStroops));
    if (fee < 1n || fee > 9223372036854775807n)
      throw new RangeError('Invalid fee-bump maximum fee');
    return fee.toString();
  }

  async function submit(server, innerTransaction, rootKeypair, networkPassphrase, maxFeeStroops) {
    const sdk = window.StellarSdk;
    if (!sdk?.TransactionBuilder?.buildFeeBumpTransaction)
      throw new Error('Stellar SDK does not support fee-bump transactions');
    if (!server || !innerTransaction || !rootKeypair || !networkPassphrase)
      throw new Error('Missing fee-bump transaction inputs');
    if (!innerTransaction.signatures?.length)
      throw new Error('Inner transaction must be signed before fee bump');
    const fee = validateFee(maxFeeStroops);
    const wrapped = sdk.TransactionBuilder.buildFeeBumpTransaction(
      rootKeypair.publicKey(), fee, innerTransaction, networkPassphrase);
    wrapped.sign(rootKeypair);
    return server.submitTransaction(wrapped);
  }

  return Object.freeze({ submit, validateFee });
})();
