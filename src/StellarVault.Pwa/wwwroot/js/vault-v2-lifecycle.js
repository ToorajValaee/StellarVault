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

// Semantic light/dark theme. Keep theme preference independent of vault secrets.
window.stellarVaultTheme = (() => {
  const key = 'stellar-vault-theme';
  const normalize = value => value === 'dark' || value === 'amber' ? 'dark' : 'light';
  function apply(value) {
    const next = normalize(value);
    document.documentElement.dataset.theme = next;
    document.documentElement.style.colorScheme = next;
    try { localStorage.setItem(key, next); } catch (_) {}
    return next;
  }
  function initialize() {
    let saved = 'light';
    try { saved = localStorage.getItem(key) || saved; } catch (_) {}
    return apply(saved);
  }
  function toggle() {
    return apply(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
  }
  return Object.freeze({ initialize, toggle });
})();


// Dialog focus isolation: modal approvals/account dialogs own keyboard and pointer
// interaction while open. The record inspector is deliberately nonmodal.
(() => {
  const guarded = new Map();
  let active = null;
  let previouslyFocused = null;
  const selector = '.approval-modal-backdrop, .modal-backdrop:not(.sv-record-inspector-backdrop)';
  function sync() {
    const modal = document.querySelector('.approval-modal-backdrop') ||
      document.querySelector('.sv-web-shell .modal-backdrop:not(.sv-record-inspector-backdrop)');
    const content = modal?.querySelector('[role="dialog"]');
    if (modal === active && modal && content) return;
    if (!modal) {
      for (const [element, initial] of guarded) element.inert = initial;
      guarded.clear();
      active = null;
      if (previouslyFocused?.isConnected) previouslyFocused.focus({ preventScroll: true });
      previouslyFocused = null;
      return;
    }
    if (!active) previouslyFocused = document.activeElement;
    active = modal;
    const shell = modal.closest('.sv-web-shell');
    const editorApproval = !!modal.closest('.sv-editor-sidecar');
    const affected = editorApproval
      ? [shell?.querySelector('.sv-pwa-stage'), shell?.querySelector('.sv-pwa-rail'),
         shell?.querySelector('.sv-adaptive-header'), shell?.querySelector('.sv-pwa-mobile-tabs')]
      : [shell?.querySelector('.sv-pwa-app'), shell?.querySelector('.sv-adaptive-header')];
    for (const element of affected) {
      if (element && !guarded.has(element) && !element.contains(modal)) {
        guarded.set(element, element.inert);
        element.inert = true;
      }
    }
    modal.querySelectorAll('details.sv-object-actions[open], details.sv-inspector-more[open]')
      .forEach(element => element.removeAttribute('open'));
    const focusTarget = content?.querySelector('button.close-button:not(:disabled), button:not(:disabled), a[href], input:not(:disabled)');
    if (focusTarget) focusTarget.focus({ preventScroll: true });
  }
  document.addEventListener('focusin', event => {
    if (!active || active.contains(event.target)) return;
    const focusTarget = active.querySelector('[role="dialog"] button:not(:disabled), [role="dialog"] a[href]');
    if (focusTarget) focusTarget.focus({ preventScroll: true });
  }, true);
  document.addEventListener('keydown', event => {
    if (!active || event.key !== 'Tab') return;
    const controls = [...active.querySelectorAll(
      'button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])'
    )].filter(element => element.getClientRects().length > 0);
    if (!controls.length) { event.preventDefault(); return; }
    const first = controls[0], last = controls[controls.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault(); last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault(); first.focus();
    }
  }, true);
  const observer = new MutationObserver(sync);
  if (document.body) observer.observe(document.body, { childList: true, subtree: true });
  else document.addEventListener('DOMContentLoaded', () => {
    observer.observe(document.body, { childList: true, subtree: true });
    sync();
  }, { once: true });
})();
