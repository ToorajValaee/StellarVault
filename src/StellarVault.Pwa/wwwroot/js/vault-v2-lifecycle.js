// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// JS events notify .NET; pagehide also causes the document to lose its managed session.
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
    const visibility = () => { if (document.visibilityState === 'hidden') lock(); };
    const pagehide = () => lock();
    const freeze = () => lock();
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('pagehide', pagehide);
    document.addEventListener('freeze', freeze);
    instance = () => {
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('pagehide', pagehide);
      document.removeEventListener('freeze', freeze);
    };
    if (document.visibilityState === 'hidden') lock();
  }
  function detach() {
    if (instance) instance();
    instance = null;
  }
  return Object.freeze({ attach, detach });
})();
