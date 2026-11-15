// Only receives already-authenticated plaintext from an unlocked v2 vault.
// Modern Blazor byte[] interop sends Uint8Array; accept base64 for compatibility.
window.stellarVaultV2Download = Object.freeze({
  pick() {
    return new Promise((resolve, reject) => {
      const input = document.createElement('input');
      input.type = 'file';
      input.accept = '*/*';
      input.hidden = true;
      document.body.append(input);
      let settled = false;
      const cleanup = () => { settled = true; input.remove(); };
      const finish = value => {
        if (settled) return;
        cleanup();
        resolve(value);
      };
      const fail = error => {
        if (settled) return;
        cleanup();
        reject(error);
      };
      input.addEventListener('cancel', () => finish(null), { once: true });
      input.addEventListener('change', async () => {
        const file = input.files?.[0];
        if (!file) { finish(null); return; }
        if (file.size <= 0 || file.size > 1024 * 1024) {
          fail(new RangeError('Select a non-empty file no larger than 1 MiB'));
          return;
        }
        try {
          const bytes = new Uint8Array(await file.arrayBuffer());
          let contentBase64;
          try {
            const chunks = [];
            for (let offset = 0; offset < bytes.length; offset += 0x8000)
              chunks.push(String.fromCharCode(...bytes.subarray(offset, offset + 0x8000)));
            contentBase64 = btoa(chunks.join(''));
          } finally {
            bytes.fill(0);
          }
          finish({
            fileName: file.name,
            contentType: file.type || 'application/octet-stream',
            contentBase64
          });
        } catch (error) {
          fail(error);
        }
      }, { once: true });
      try { input.click(); }
      catch (error) { fail(error); }
    });
  },
  save(filename, payload) {
    let data;
    if (payload instanceof Uint8Array) {
      data = payload.slice();
    } else if (typeof payload === 'string') {
      if (!/^[A-Za-z0-9+/]*={0,2}$/.test(payload)) throw new TypeError('Invalid binary base64');
      const raw = atob(payload);
      data = new Uint8Array(raw.length);
      for (let i = 0; i < raw.length; i++) data[i] = raw.charCodeAt(i);
    } else {
      throw new TypeError('Expected binary file content');
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
