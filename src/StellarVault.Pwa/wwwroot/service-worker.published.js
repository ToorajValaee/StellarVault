self.importScripts('./service-worker-assets.js');

const cachePrefix = 'stellar-vault-cache-';
const cacheName = `${cachePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm$/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.png$/, /\.svg$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    const cache = await caches.open(cacheName);
    // Activate only after the entire new offline bundle is present.
    // A partial precache must not replace a previously working release.
    try {
        await cache.addAll(assetsRequests);
        await self.skipWaiting();
    } catch (error) {
        await caches.delete(cacheName);
        throw error;
    }
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cachePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function onFetch(event) {
    if (event.request.method !== 'GET')
        return fetch(event.request);

    const requestUrl = new URL(event.request.url);
    if (requestUrl.origin !== self.location.origin)
        return fetch(event.request);

    const cache = await caches.open(cacheName);
    const isNavigation = event.request.mode === 'navigate';
    const cacheKey = isNavigation ? 'index.html' : event.request;
    const cachedResponse = await cache.match(cacheKey);

    return cachedResponse || fetch(event.request);
}
