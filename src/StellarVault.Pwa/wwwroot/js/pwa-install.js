(() => {
    window.__stellarVaultInstallPrompt = null;
    window.__stellarVaultInstallEventSeen = false;
    window.addEventListener('beforeinstallprompt', event => {
        event.preventDefault();
        window.__stellarVaultInstallPrompt = event;
        window.__stellarVaultInstallEventSeen = true;
        window.dispatchEvent(new CustomEvent('stellarvault:install-ready'));
    }, { capture: true });
    window.addEventListener('appinstalled', () => {
        window.__stellarVaultInstallPrompt = null;
        window.dispatchEvent(new CustomEvent('stellarvault:installed'));
    });
    if ('serviceWorker' in navigator) {
        const showUpdate = () => {
            const show = () => {
                const notice = document.getElementById('pwa-update-notice');
                if (notice) notice.hidden = false;
            };
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', show, { once: true });
            else show();
            window.dispatchEvent(new CustomEvent('stellarvault:update-ready'));
        };
        window.__stellarVaultWorkerRegistration = navigator.serviceWorker
            .register('service-worker.js', { updateViaCache: 'none' })
            .then(registration => {
                if (registration.waiting && navigator.serviceWorker.controller) showUpdate();
                registration.addEventListener('updatefound', () => {
                    const installing = registration.installing;
                    installing?.addEventListener('statechange', () => {
                        if (installing.state === 'installed' && navigator.serviceWorker.controller) showUpdate();
                    });
                });
                return registration;
            });
    }
})();
