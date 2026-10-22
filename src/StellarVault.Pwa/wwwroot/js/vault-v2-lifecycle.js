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
    const visibility = () => {
      if (document.visibilityState === 'hidden')
        Promise.resolve(dotnet.invokeMethodAsync('SuspendForPrivacy')).catch(() => {});
    };
    const pagehide = () => lock();
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('pagehide', pagehide);
    instance = () => {
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('pagehide', pagehide);
    };
  }
  function detach() {
    if (instance) instance();
    instance = null;
  }
  return Object.freeze({ attach, detach });
})();
