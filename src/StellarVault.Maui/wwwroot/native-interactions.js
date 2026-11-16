// Native-host navigation conveniences. No keys, content, signing or storage access.
(() => {
  document.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.repeat || event.altKey || event.isComposing) return;
    const shell = document.querySelector('.native-windows, .native-maccatalyst');
    if (!shell || !(event.ctrlKey || event.metaKey)) return;
    const destinations = {'1':'stellar','2':'local','3':'sync','4':'settings'};
    const destination = destinations[event.key];
    if (destination) {
      const button = shell.querySelector('[data-native-destination="'+destination+'"]');
      if (button && !button.disabled) { event.preventDefault(); button.click(); }
      return;
    }
    if (event.key.toLowerCase() === 'f') {
      const input = [...shell.querySelectorAll('input[type="search"]')].find(e => e.getClientRects().length && !e.disabled);
      if (input) { event.preventDefault(); input.focus(); input.select(); }
    }
  });
})();
