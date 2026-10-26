// Browser lifecycle bridge for fresh v2. Never persists keys or credentials.
// Switching tabs preserves the in-memory session. Pagehide still locks it.
// This trades stronger background isolation for usability on ordinary app switches.
// Five-second focus-loss privacy guard. Remaining in the tab never locks. The privacy lock only
// clears the active local key; resuming asks for the vault passphrase.
// Explicit-lock policy: neither focus changes nor pagehide request a vault lock.
// A full reload naturally destroys the in-memory WASM session and signer.
// No key material is persisted by this lifecycle bridge.
window.stellarVaultV2Lifecycle = (() => {
  function attach(_dotnet) {}
  function detach() {}
  function setCredentialPrompt(_value) {}
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
