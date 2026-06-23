// Development service worker. Published builds use service-worker.published.js.
// This handler deliberately avoids caching framework files while still behaving
// as a real fetch-capable service worker during local Android install testing.
self.addEventListener('install', event => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') {
        return;
    }

    event.respondWith(fetch(event.request));
});
