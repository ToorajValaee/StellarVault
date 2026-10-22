// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide/freeze still lock it.
// This trades stronger background isolation for usability on ordinary app switches.
window.stellarVaultV2Lifecycle = (() => {
  let instance = null;
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
    const pagehide = () => lock();
    const freeze = () => lock();
    window.addEventListener('pagehide', pagehide);
    document.addEventListener('freeze', freeze);
    instance = () => {
      window.removeEventListener('pagehide', pagehide);
      document.removeEventListener('freeze', freeze);
    };
  }
  function detach() {
    if (instance) instance();
    instance = null;
  }
  return Object.freeze({ attach, detach });
})();
