/** Scenarios executes par la suite pytest ; chaque fonction renvoie un objet JSON. */
import { chargerApp } from './harness.mjs';

const scenarios = {
  normalisation() {
    const { api } = chargerApp();
    return {
      doublons: api.normaliserAdresses(['http://a:1', 'http://a:1/', ' http://a:1 ']),
      vides: api.normaliserAdresses(['', '   ', null, undefined, 'http://b:2']),
      ordre: api.normaliserAdresses(['http://local', 'https://distant']),
    };
  },

  schemaWebsocket() {
    const { api } = chargerApp();
    return {
      clair: api.adresseWebSocket('http://192.168.1.42:8777', 'abc'),
      chiffre: api.adresseWebSocket('https://pc.exemple.fr', 'a/b+c'),
      sansJeton: api.adresseWebSocket('http://192.168.1.42:8777', ''),
      relative: api.adresseWebSocket('mon-pc', 'abc'),
      vide: api.adresseWebSocket('', 'abc'),
      mauvaisSchema: api.adresseWebSocket('ftp://pc.exemple.fr', 'abc'),
    };
  },

  migrationAncienFormat() {
    const { api } = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({ url: 'http://192.168.1.42:8777', token: 'vieux' }),
      },
    });
    return { urls: api.reglages.urls, token: api.reglages.token };
  },

  jetonDansLeFragment() {
    const { api, magasin } = chargerApp({
      origin: 'https://pc.exemple.fr',
      hash: '#token=nouveau',
      stockage: {
        'overmlay.reglages': JSON.stringify({ urls: ['http://192.168.1.42:8777'], token: 'ancien' }),
      },
    });
    return {
      urls: api.reglages.urls,
      token: api.reglages.token,
      persiste: JSON.parse(magasin.get('overmlay.reglages')),
    };
  },

  basculeVersLAdresseDistante() {
    const app = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({
          urls: ['http://192.168.1.42:8777', 'https://pc.exemple.fr'],
          token: 'jeton',
        }),
      },
    });
    const etapes = [];
    const noter = (delai) =>
      etapes.push({ url: app.dernierSocket().url, etat: app.api.elements.etat.textContent, delai });

    noter(0); // premiere tentative : adresse locale
    app.dernierSocket().fermer(1006); // hors du reseau domestique
    const delai1 = app.avancer();
    noter(delai1); // doit avoir bascule sur l'adresse distante

    app.dernierSocket().ouvrir();
    return {
      etapes,
      etatConnecte: app.api.elements.etat.textContent,
      indexFinal: app.api.etat.indexAdresse,
    };
  },

  reculExponentielApresUnTourComplet() {
    const app = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({
          urls: ['http://local:8777', 'https://distant'],
          token: 'jeton',
        }),
      },
    });
    const delais = [];
    for (let i = 0; i < 6; i += 1) {
      app.dernierSocket().fermer(1006);
      delais.push(app.avancer());
    }
    return { delais, urls: app.sockets.map((s) => s.url.split('?')[0]) };
  },

  jetonRefuseNeBoucle() {
    const app = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({ urls: ['http://local:8777'], token: 'faux' }),
      },
    });
    app.dernierSocket().fermer(1008); // refus applicatif de l'agent
    return {
      reconnexionsProgrammees: app.minuteries.length,
      etat: app.api.elements.etat.textContent,
      messageAffiche: app.elements.get('message-reglages').textContent,
    };
  },

  adresseInvalidePasseALaSuivante() {
    const app = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({
          urls: ['mon-pc-mal-saisi', 'https://pc.exemple.fr'],
          token: 'jeton',
        }),
      },
    });
    const avant = app.sockets.length;
    const delai = app.avancer();
    app.dernierSocket().ouvrir();
    return {
      socketsAvant: avant,
      delai,
      url: app.dernierSocket().url,
      etat: app.api.elements.etat.textContent,
    };
  },

  uneSeuleAdresseNAffichePasDEtiquette() {
    const app = chargerApp({
      stockage: {
        'overmlay.reglages': JSON.stringify({ urls: ['http://local:8777'], token: 'jeton' }),
      },
    });
    app.dernierSocket().ouvrir();
    return { etat: app.api.elements.etat.textContent };
  },
};

const nom = process.argv[2];
if (!scenarios[nom]) {
  console.error(`scenario inconnu : ${nom}`);
  process.exit(2);
}
process.stdout.write(JSON.stringify(scenarios[nom]()));
