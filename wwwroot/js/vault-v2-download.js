// Download only already-authenticated plaintext from the unlocked v2 vault.
// Blazor marshals byte[] arguments as base64 strings through JSON interop.
window.stellarVaultV2Download = Object.freeze({
  save(filename, base64) {
    if (typeof base64 !== 'string') throw new TypeError('Expected base64 file content');
    if (base64.length > 2 * 1024 * 1024) throw new RangeError('Download exceeds limit');
    const raw = atob(base64);
    const data = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) data[i] = raw.charCodeAt(i);
    const safeName = typeof filename === 'string'
      ? filename.replace(/[\\/\x00-\x1f\x7f]/g, '_').slice(0, 160)
      : 'stellar-vault-record';
    const url = URL.createObjectURL(new Blob([data], {type: 'application/octet-stream'}));
    try {
      const link = document.createElement('a');
      link.href = url;
      link.download = safeName || 'stellar-vault-record';
      document.body.append(link);
      link.click();
      link.remove();
    } finally {
      // Keep the object URL alive long enough for navigation to capture it.
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      data.fill(0);
    }
  }
});
