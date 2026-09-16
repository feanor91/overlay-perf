/**
 * Harnais d'execution de l'application mobile hors navigateur.
 *
 * Fournit juste assez de DOM, de stockage et de WebSocket factices pour charger
 * `app.js` et piloter sa logique de connexion depuis les tests. Les minuteries
 * sont simulees : un scenario de bascule s'execute sans attendre.
 */
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';

export function chargerApp({ origin = 'http://192.168.1.42:8777', stockage = {}, hash = '' } = {}) {
  const elements = new Map();
  const faireElement = () => ({
    value: '',
    textContent: '',
    className: '',
    title: '',
    style: {},
    hidden: false,
    checked: false,
    children: [],
    addEventListener(type, handler) {
      (this.handlers ||= {})[type] = handler;
    },
    setAttribute(nom, valeur) {
      (this.attributs ||= {})[nom] = valeur;
    },
    appendChild(enfant) {
      this.children.push(enfant);
      return enfant;
    },
    append(...enfants) {
      this.children.push(...enfants);
    },
  });

  const sockets = [];
  const minuteries = [];
  let prochainId = 1;

  const magasin = new Map(Object.entries(stockage));

  const contexte = {
    console,
    JSON,
    Math,
    Set,
    Map,
    URL,
    URLSearchParams,
    Object,
    Array,
    String,
    Number,
    Boolean,
    Promise,
    encodeURIComponent,
    document: {
      getElementById(id) {
        if (!elements.has(id)) elements.set(id, faireElement());
        return elements.get(id);
      },
      createElement: faireElement,
      createElementNS: faireElement,
      addEventListener() {},
      visibilityState: 'visible',
    },
    window: {
      location: { origin, hash, pathname: '/' },
      addEventListener() {},
    },
    history: { replaceState() {} },
    navigator: {},
    localStorage: {
      getItem: (cle) => (magasin.has(cle) ? magasin.get(cle) : null),
      setItem: (cle, valeur) => magasin.set(cle, valeur),
      removeItem: (cle) => magasin.delete(cle),
    },
    setTimeout(rappel, delai) {
      const id = prochainId++;
      minuteries.push({ id, rappel, delai });
      return id;
    },
    clearTimeout(id) {
      const index = minuteries.findIndex((m) => m.id === id);
      if (index >= 0) minuteries.splice(index, 1);
    },
  };
  contexte.globalThis = contexte;
  contexte.WebSocket = class {
    static CONNECTING = 0;
    static OPEN = 1;
    static CLOSING = 2;
    static CLOSED = 3;
    constructor(url) {
      this.url = url;
      this.readyState = 0;
      sockets.push(this);
    }
    close() {
      this.readyState = 3;
    }
    ouvrir() {
      this.readyState = 1;
      this.onopen?.();
    }
    fermer(code = 1006) {
      this.readyState = 3;
      this.onclose?.({ code });
    }
  };

  createContext(contexte);
  const source = readFileSync(new URL('../../src/overlay/webapp/app.js', import.meta.url), 'utf8');
  const epilogue = `
    globalThis.__test = {
      get etat() { return etat; },
      get reglages() { return reglages; },
      normaliserAdresses,
      adresseWebSocket,
      connecter,
      elements,
    };
  `;
  runInContext(source + epilogue, contexte);

  return {
    api: contexte.__test,
    sockets,
    minuteries,
    elements,
    magasin,
    /** Declenche la minuterie de reconnexion en attente. */
    avancer() {
      const minuterie = minuteries.shift();
      if (!minuterie) throw new Error('aucune reconnexion programmee');
      minuterie.rappel();
      return minuterie.delai;
    },
    dernierSocket: () => sockets[sockets.length - 1],
  };
}
