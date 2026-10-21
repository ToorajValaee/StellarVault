// Development worker: do not intercept fetches. Network failures must surface
// directly in the page instead of becoming rejected respondWith promises.
// Published builds use service-worker.published.js for offline caching.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
