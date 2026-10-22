// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide still locks it.
// This trades stronger background isolation for usability on ordinary app switches.
window.stellarVaultV2Lifecycle = (() => {
  let instance = null;
  // Native file pickers temporarily take browser focus. Suppress the privacy
  // lock only for that browser-owned picker, not for arbitrary app switches.
  let filePickerOpen = false;
  let pickerReturnTimer = null;
  const resetFilePicker = () => {
    if (pickerReturnTimer) clearTimeout(pickerReturnTimer);
    pickerReturnTimer = null;
    filePickerOpen = false;
  };
  const onFileInputClick = (event) => {
    if (event.target instanceof HTMLInputElement && event.target.type === 'file') {
      if (pickerReturnTimer) clearTimeout(pickerReturnTimer);
      filePickerOpen = true;
    }
  };
  const onFileInputChange = (event) => {
    if (event.target instanceof HTMLInputElement && event.target.type === 'file')
      resetFilePicker();
  };
  const onPickerReturn = () => {
    if (filePickerOpen) {
      if (pickerReturnTimer) clearTimeout(pickerReturnTimer);
      pickerReturnTimer = setTimeout(resetFilePicker, 750);
    }
  };
  function attach(dotnet) {
    detach();
    let pendingLock = false;
    const lock = () => {
      if (pendingLock) return;
      pendingLock = true;
      Promise.resolve(dotnet.invokeMethodAsync('LockForBrowserLifecycle'))
        .catch(() => {})
        .finally(() => { pendingLock = false; });
    };
    const visibility = () => {
      if (!filePickerOpen && document.visibilityState === 'hidden')
        Promise.resolve(dotnet.invokeMethodAsync('SuspendForPrivacy')).catch(() => {});
    };
    const blur = () => filePickerOpen ? undefined : Promise.resolve(dotnet.invokeMethodAsync('SuspendForPrivacy')).catch(() => {});
    const pagehide = () => lock();
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('blur', blur);
    document.addEventListener('click', onFileInputClick, true);
    document.addEventListener('change', onFileInputChange, true);
    window.addEventListener('focus', onPickerReturn);
    window.addEventListener('pagehide', pagehide);
    instance = () => {
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('blur', blur);
      document.removeEventListener('click', onFileInputClick, true);
      document.removeEventListener('change', onFileInputChange, true);
      window.removeEventListener('focus', onPickerReturn);
      resetFilePicker();
      window.removeEventListener('pagehide', pagehide);
    };
  }
  function detach() {
    if (instance) instance();
    instance = null;
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
