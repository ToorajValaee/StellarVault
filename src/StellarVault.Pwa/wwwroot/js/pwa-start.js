(async () => {
    const timeout = task => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('Application bundle verification timed out.')), 45000);
        Promise.resolve(task).then(value => { clearTimeout(timer); resolve(value); }, error => { clearTimeout(timer); reject(error); });
    });
    try {
        if (!('serviceWorker' in navigator) || !window.__stellarVaultWorkerRegistration)
            throw new Error('A secure browser with service-worker support is required.');
        await timeout(window.__stellarVaultWorkerRegistration);
        await timeout(navigator.serviceWorker.ready);
        // The first bootstrap has not opened a vault or signer. Reload only
        // this uncontrolled startup so all later code comes from one verified release.
        if (!navigator.serviceWorker.controller) {
            window.location.reload();
            return;
        }
        await Blazor.start();
    } catch {
        const line = document.querySelector('.boot-line');
        if (line) line.textContent = 'Unable to verify the application bundle. Check your connection and browser storage, then reload.';
        const error = document.getElementById('blazor-error-ui');
        if (error) error.style.display = 'block';
        console.error('StellarVault startup verification failed.');
    }
})();
