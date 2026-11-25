(() => {
        const root = document.documentElement;
        let drag = null;
        const clamp = value => Math.max(320, Math.min(value, Math.min(720, innerWidth * .58)));
        const setWidth = width => root.style.setProperty('--sv-record-inspector-width', Math.round(clamp(width)) + 'px');
        document.addEventListener('pointerdown', event => {
          const handle = event.target.closest?.('.sv-inspector-resize-handle');
          if (!handle || innerWidth < 1100) return;
          drag = { id: event.pointerId };
          handle.setPointerCapture(event.pointerId);
          event.preventDefault();
        });
        document.addEventListener('pointermove', event => {
          if (!drag || drag.id !== event.pointerId) return;
          setWidth(innerWidth - event.clientX);
        });
        const release = event => { if (drag?.id === event.pointerId) drag = null; };
        document.addEventListener('pointerup', release);
        document.addEventListener('pointercancel', release);
        document.addEventListener('keydown', event => {
          const handle = event.target.closest?.('.sv-inspector-resize-handle');
          if (!handle || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
          const current = parseFloat(getComputedStyle(root).getPropertyValue('--sv-record-inspector-width')) || 400;
          setWidth(current + (event.key === 'ArrowLeft' ? 24 : -24));
          event.preventDefault();
        });
        addEventListener('resize', () => {
          const current = parseFloat(root.style.getPropertyValue('--sv-record-inspector-width'));
          if (current) setWidth(current);
        });
      })();
