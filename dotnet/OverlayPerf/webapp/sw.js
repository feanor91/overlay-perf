/**
 * Service worker : rend l'application installable et ouvrable hors reseau.
 *
 * La coquille (HTML, CSS, JS, icones) est mise en cache ; les appels a l'API et le
 * WebSocket ne le sont jamais, une mesure materielle perimee n'ayant aucune valeur.
 */
'use strict';

const CACHE = 'overlay-v2';
const COQUILLE = [
  '/',
  '/app/index.html',
  '/app/style.css',
  '/app/app.js',
  '/app/manifest.webmanifest',
  '/app/icon-192.png',
  '/app/icon-512.png',
];

self.addEventListener('install', (evenement) => {
  evenement.waitUntil(
    caches.open(CACHE).then((cache) => cache.addAll(COQUILLE)).then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (evenement) => {
  evenement.waitUntil(
    caches
      .keys()
      .then((cles) => Promise.all(cles.filter((cle) => cle !== CACHE).map((cle) => caches.delete(cle))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (evenement) => {
  const requete = evenement.request;
  if (requete.method !== 'GET') return;

  const url = new URL(requete.url);
  if (url.pathname.startsWith('/api/') || url.pathname === '/ws') return;
  if (url.origin !== self.location.origin) return;

  evenement.respondWith(
    caches.match(requete).then((enCache) => {
      const reseau = fetch(requete)
        .then((reponse) => {
          if (reponse && reponse.ok) {
            const copie = reponse.clone();
            caches.open(CACHE).then((cache) => cache.put(requete, copie));
          }
          return reponse;
        })
        .catch(() => enCache);
      // Cache d'abord pour un demarrage instantane, rafraichi en arriere-plan.
      return enCache || reseau;
    })
  );
});
