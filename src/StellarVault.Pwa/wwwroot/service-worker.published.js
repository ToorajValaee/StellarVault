self.importScripts('./service-worker-assets.js');

const cachePrefix = 'stellar-vault-cache-';
const cacheName = `${cachePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.wasm$/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.png$/, /\.svg$/, /\.webmanifest$/, /\.dat$/, /\.woff2?$/];
const offlineAssetsExclude = [/^service-worker(?:\.published)?\.js$/, /^service-worker-assets\.js$/];
const scopeUrl = new URL(self.registration.scope);
const releaseAssets = new Map(self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    .map(asset => {
        const url = new URL(asset.url, scopeUrl);
        if (url.origin !== scopeUrl.origin || !url.pathname.startsWith(scopeUrl.pathname) || url.search || url.hash)
            throw new Error('Invalid release asset URL.');
        return [url.pathname, { url: url.href, hash: asset.hash }];
    }));

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

async function onInstall() {
    const assetsRequests = Array.from(releaseAssets.values(), asset =>
        new Request(asset.url, { integrity: asset.hash, cache: 'no-cache', credentials: 'omit', redirect: 'error' }));
    const existed = (await caches.keys()).includes(cacheName);
    const cache = await caches.open(cacheName);
    try {
        await cache.addAll(assetsRequests);
        // No skipWaiting: an unlocked tab must keep its original code and signer.
        // The browser activates the new release only after all older tabs close.
    } catch (error) {
        if (!existed) await caches.delete(cacheName);
        throw error;
    }
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cachePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    // Do not claim existing uncontrolled clients. The bootstrap reloads safely
    // before opening any vault, after the first verified installation is ready.
}

async function onFetch(event) {
    if (event.request.method !== 'GET' || event.request.headers.has('Authorization'))
        return fetch(event.request);
    const requestUrl = new URL(event.request.url);
    if (requestUrl.origin !== scopeUrl.origin || !requestUrl.pathname.startsWith(scopeUrl.pathname))
        return fetch(event.request);
    const relativePath = requestUrl.pathname.slice(scopeUrl.pathname.length);
    if (/^(v1|api|health)(\/|$)/.test(relativePath)) return fetch(event.request);
    const cache = await caches.open(cacheName);
    if (event.request.mode === 'navigate') {
        // Always use the matching integrity-verified shell. A fresh network
        // index combined with an older runtime is an unsafe mixed release.
        const shell = await cache.match('index.html');
        if (!shell) throw new Error('Verified release shell is missing.');
        return shell;
    }
    const asset = releaseAssets.get(requestUrl.pathname);
    if (!asset) return fetch(event.request);
    // Only known public release files ignore cache-busting query strings.
    const cached = await cache.match(asset.url);
    return cached || fetch(new Request(asset.url, {
        integrity: asset.hash, cache: 'no-cache', credentials: 'omit', redirect: 'error', signal: event.request.signal
    }));
}
