// Only receives already-authenticated plaintext from an unlocked v2 vault.
// Modern Blazor byte[] interop sends Uint8Array; accept base64 for compatibility.
window.stellarVaultV2Download = Object.freeze({
  save(filename, payload) {
    let data;
    if (payload instanceof Uint8Array) {
      data = payload.slice();
    } else if (typeof payload === 'string') {
      if (payload.length > 2 * 1024 * 1024) throw new RangeError('Download exceeds limit');
      const raw = atob(payload);
      data = new Uint8Array(raw.length);
      for (let i = 0; i < raw.length; i++) data[i] = raw.charCodeAt(i);
    } else {
      throw new TypeError('Expected binary file content');
    }
    if (data.byteLength > 1024 * 1024) {
      data.fill(0);
      throw new RangeError('Download exceeds limit');
    }
    const safeName = typeof filename === 'string'
      ? filename.replace(/[\\/\x00-\x1f\x7f]/g, '_').slice(0, 160)
      : 'stellar-vault-record';
    const url = URL.createObjectURL(new Blob([data], { type: 'application/octet-stream' }));
    try {
      const link = document.createElement('a');
      link.href = url;
      link.download = safeName || 'stellar-vault-record';
      document.body.append(link);
      link.click();
      link.remove();
    } finally {
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      // Do not zero the interop-provided payload, which may be shared by the runtime.
      data.fill(0);
    }
  }
});
