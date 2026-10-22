// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide still locks it.
// This trades stronger background isolation for usability on ordinary app switches.
// Five-second inactivity and focus-loss privacy guard. The privacy lock only
// clears the active local key; resuming asks for the vault passphrase.
window.stellarVaultV2Lifecycle = (() => {
  const timeoutMs = 5000;
  let detachCurrent = null;
  let filePickerOpen = false;
  let pickerReturnTimer = null;

  function attach(dotnet) {
    detach();
    let idleTimer = null;
    let awayTimer = null;
    let lastActivity = Date.now();
    let awaySince = null;
    let exited = false;

    const suspend = () => {
      if (exited || filePickerOpen) return;
      Promise.resolve(dotnet.invokeMethodAsync('SuspendForPrivacy')).catch(() => {});
    };
    const clearIdle = () => { if (idleTimer !== null) clearTimeout(idleTimer); idleTimer = null; };
    const clearAway = () => { if (awayTimer !== null) clearTimeout(awayTimer); awayTimer = null; };

    const startIdle = () => {
      clearIdle();
      if (filePickerOpen || awaySince !== null || exited) return;
      const remaining = Math.max(0, timeoutMs - (Date.now() - lastActivity));
      idleTimer = setTimeout(() => {
        if (Date.now() - lastActivity >= timeoutMs) suspend();
        else startIdle();
      }, remaining);
    };
    const active = () => {
      if (exited || filePickerOpen || awaySince !== null) return;
      lastActivity = Date.now();
      startIdle();
    };
    const goAway = () => {
      if (filePickerOpen || exited || awaySince !== null) return;
      awaySince = Date.now();
      clearIdle();
      clearAway();
      awayTimer = setTimeout(() => {
        if (awaySince !== null && Date.now() - awaySince >= timeoutMs) suspend();
      }, timeoutMs);
    };
    const returnFocus = () => {
      if (exited) return;
      if (filePickerOpen) {
        if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
        pickerReturnTimer = setTimeout(() => {
          pickerReturnTimer = null;
          filePickerOpen = false;
          awaySince = null;
          clearAway();
          lastActivity = Date.now();
          startIdle();
        }, 750);
        return;
      }
      if (awaySince !== null && Date.now() - awaySince >= timeoutMs) suspend();
      awaySince = null;
      clearAway();
      lastActivity = Date.now();
      startIdle();
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
      clearIdle();
      clearAway();
    };
    const pickerDone = event => {
      const target = event.target;
      if (!(target instanceof HTMLInputElement) || target.type !== 'file') return;
      if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = null;
      filePickerOpen = false;
      awaySince = null;
      lastActivity = Date.now();
      startIdle();
    };
    const pagehide = () => {
      exited = true;
      clearIdle();
      clearAway();
      Promise.resolve(dotnet.invokeMethodAsync('LockForBrowserLifecycle')).catch(() => {});
    };

    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('blur', goAway);
    window.addEventListener('focus', returnFocus);
    window.addEventListener('pagehide', pagehide);
    document.addEventListener('pointerdown', active, true);
    document.addEventListener('keydown', active, true);
    document.addEventListener('input', active, true);
    document.addEventListener('scroll', active, true);
    document.addEventListener('click', pickerStart, true);
    document.addEventListener('change', pickerDone, true);
    startIdle();

    detachCurrent = () => {
      exited = true;
      clearIdle();
      clearAway();
      if (pickerReturnTimer !== null) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = null;
      filePickerOpen = false;
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('blur', goAway);
      window.removeEventListener('focus', returnFocus);
      window.removeEventListener('pagehide', pagehide);
      document.removeEventListener('pointerdown', active, true);
      document.removeEventListener('keydown', active, true);
      document.removeEventListener('input', active, true);
      document.removeEventListener('scroll', active, true);
      document.removeEventListener('click', pickerStart, true);
      document.removeEventListener('change', pickerDone, true);
    };
  }

  function detach() {
    if (detachCurrent) detachCurrent();
    detachCurrent = null;
  }
  return Object.freeze({ attach, detach });
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
