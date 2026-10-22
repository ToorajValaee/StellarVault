// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide still locks it.
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
    window.addEventListener('pagehide', pagehide);
    instance = () => {
      window.removeEventListener('pagehide', pagehide);
    };
  }
  function detach() {
    if (instance) instance();
    instance = null;
  }
  return Object.freeze({ attach, detach });
})();
