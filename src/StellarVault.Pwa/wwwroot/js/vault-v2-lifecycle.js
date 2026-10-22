// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide still locks it.
// This trades stronger background isolation for usability on ordinary app switches.
// Five-second focus-loss privacy guard. Remaining in the tab never locks. The privacy lock only
// clears the active local key; resuming asks for the vault passphrase.
window.stellarVaultV2Lifecycle = (() => {
  const timeoutMs = 5000;
  let detachCurrent = null;
  let filePickerOpen = false;
  let credentialPromptOpen = false;
  let pickerReturnTimer = null;

  function attach(dotnet) {
    detach();
    let awayTimer = null;
    let awaySince = null;
    let exited = false;

    const suspend = () => {
      if (exited || filePickerOpen || credentialPromptOpen) return;
      Promise.resolve(dotnet.invokeMethodAsync('SuspendForPrivacy')).catch(() => {});
    };
    const clearAway = () => { if (awayTimer !== null) clearTimeout(awayTimer); awayTimer = null; };

    const goAway = () => {
      if (filePickerOpen || credentialPromptOpen || exited || awaySince !== null) return;
      awaySince = Date.now();
      clearAway();
      awayTimer = setTimeout(() => {
        if (awaySince !== null && Date.now() - awaySince >= timeoutMs) suspend();
      }, timeoutMs);
    };
    const returnFocus = () => {
      if (exited) return;
      if (filePickerOpen || credentialPromptOpen) {
        if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
        pickerReturnTimer = setTimeout(() => {
          pickerReturnTimer = null;
          filePickerOpen = false;
          awaySince = null;
          clearAway();
        }, 750);
        return;
      }
      if (awaySince !== null && Date.now() - awaySince >= timeoutMs) suspend();
      awaySince = null;
      clearAway();
    };
    const visibility = () => {
      if (document.visibilityState === 'hidden') goAway();
      else returnFocus();
    };
    const pickerStart = event => {
      const target = event.target;
      if (!(target instanceof HTMLInputElement) || target.type !== 'file') return;
      filePickerOpen = true;
      if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = null;
      awaySince = null;
      clearAway();
    };
    const pickerDone = event => {
      const target = event.target;
      if (!(target instanceof HTMLInputElement) || target.type !== 'file') return;
      if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = null;
      filePickerOpen = false;
      awaySince = null;
    };
    const pagehide = () => {
      exited = true;
      clearAway();
      Promise.resolve(dotnet.invokeMethodAsync('LockForBrowserLifecycle')).catch(() => {});
    };

    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('blur', goAway);
    window.addEventListener('focus', returnFocus);
    window.addEventListener('pagehide', pagehide);
    document.addEventListener('click', pickerStart, true);
    document.addEventListener('change', pickerDone, true);

    detachCurrent = () => {
      exited = true;
      clearAway();
      if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = null;
      filePickerOpen = false;
      credentialPromptOpen = false;
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('blur', goAway);
      window.removeEventListener('focus', returnFocus);
      window.removeEventListener('pagehide', pagehide);
      document.removeEventListener('click', pickerStart, true);
      document.removeEventListener('change', pickerDone, true);
    };
  }

  function detach() {
    if (detachCurrent) detachCurrent();
    detachCurrent = null;
  }
  function setCredentialPrompt(value) {
    credentialPromptOpen = Boolean(value);
    if (!credentialPromptOpen) {
      // Returning from the operating system passkey sheet is not app abandonment.
      if (typeof window.dispatchEvent === 'function') window.dispatchEvent(new Event('focus'));
    }
  }
  return Object.freeze({ attach, detach, setCredentialPrompt });
})();

 // Close network settings when clicking outside the popup or pressing Escape.
document.addEventListener('pointerdown', event => {
  document.querySelectorAll('details.sv-network-menu[open]').forEach(menu => {
    if (!menu.contains(event.target)) menu.removeAttribute('open');
  });
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape')
    document.querySelectorAll('details.sv-network-menu[open]').forEach(menu => menu.removeAttribute('open'));
});

window.stellarVaultTheme = (() => {
  const key = 'stellar-vault-theme';
  function apply(value) {
    const next = value === 'amber' ? 'amber' : 'green';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem(key, next); } catch (_) {}
    return next;
  }
  function initialize() {
    let saved = 'green';
    try { saved = localStorage.getItem(key) || saved; } catch (_) {}
    return apply(saved);
  }
  function toggle() {
    return apply(document.documentElement.dataset.theme === 'amber' ? 'green' : 'amber');
  }
  return Object.freeze({ initialize, toggle });
})();
