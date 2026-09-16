/**
 * Client temps reel d'Overmlay.
 *
 * Se connecte en WebSocket a l'agent qui tourne sur le PC, affiche les mesures
 * regroupees par materiel et laisse choisir celles a montrer. Tout l'etat propre
 * au telephone (adresse, jeton, mesures masquees) vit dans localStorage.
 */
'use strict';

const CLE_REGLAGES = 'overmlay.reglages';
const CLE_MASQUES = 'overmlay.masques';
const POINTS_COURBE = 40;
const RECONNEXION_MIN = 1000;
const RECONNEXION_MAX = 15000;

const TITRES_GROUPES = {
  fps: 'Images par seconde',
  cpu: 'Processeur',
  gpu: 'Carte graphique',
  memory: 'Memoire',
  fan: 'Ventilateurs',
  storage: 'Stockage',
  network: 'Reseau',
  system: 'Systeme',
};
const ORDRE_GROUPES = ['fps', 'cpu', 'gpu', 'memory', 'fan', 'storage', 'network', 'system'];

const elements = {
  hote: document.getElementById('hote'),
  etat: document.getElementById('etat'),
  tableau: document.getElementById('tableau'),
  vide: document.getElementById('vide'),
  panneauReglages: document.getElementById('panneau-reglages'),
  panneauAffichage: document.getElementById('panneau-affichage'),
  listeMesures: document.getElementById('liste-mesures'),
  champUrl: document.getElementById('champ-url'),
  champToken: document.getElementById('champ-token'),
  champVeille: document.getElementById('champ-veille'),
  messageReglages: document.getElementById('message-reglages'),
  btnReglages: document.getElementById('btn-reglages'),
  btnAffichage: document.getElementById('btn-affichage'),
  btnConnecter: document.getElementById('btn-connecter'),
  btnOublier: document.getElementById('btn-oublier'),
  btnTout: document.getElementById('btn-tout'),
  btnRien: document.getElementById('btn-rien'),
};

const etat = {
  socket: null,
  reconnexion: RECONNEXION_MIN,
  minuteur: null,
  masques: new Set(),
  historique: new Map(),
  tuiles: new Map(),
  clesRendues: '',
  connuesMesures: new Map(),
  verrouEcran: null,
};

// --- Persistance -----------------------------------------------------------

function lireJson(cle, defaut) {
  try {
    const brut = localStorage.getItem(cle);
    return brut ? JSON.parse(brut) : defaut;
  } catch (erreur) {
    return defaut;
  }
}

function ecrireJson(cle, valeur) {
  try {
    localStorage.setItem(cle, JSON.stringify(valeur));
  } catch (erreur) {
    /* mode navigation privee : on continue sans persistance */
  }
}

function chargerReglages() {
  const enregistres = lireJson(CLE_REGLAGES, {});
  const reglages = {
    url: enregistres.url || window.location.origin,
    token: enregistres.token || '',
    veille: Boolean(enregistres.veille),
  };
  // « overmlay pair » produit une URL du type http://ip:port/#token=... : on
  // recupere le jeton puis on nettoie la barre d'adresse pour ne pas l'y laisser.
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ''));
  const jetonPartage = fragment.get('token');
  if (jetonPartage) {
    reglages.token = jetonPartage;
    reglages.url = window.location.origin;
    ecrireJson(CLE_REGLAGES, reglages);
    history.replaceState(null, '', window.location.pathname);
  }
  return reglages;
}

let reglages = chargerReglages();
etat.masques = new Set(lireJson(CLE_MASQUES, []));

// --- Connexion -------------------------------------------------------------

function adresseWebSocket(base, token) {
  let racine;
  try {
    racine = new URL(base, window.location.origin);
  } catch (erreur) {
    return null;
  }
  racine.protocol = racine.protocol === 'https:' ? 'wss:' : 'ws:';
  racine.pathname = '/ws';
  racine.hash = '';
  racine.search = token ? `?token=${encodeURIComponent(token)}` : '';
  return racine.toString();
}

function majEtat(texte, classe) {
  elements.etat.textContent = texte;
  elements.etat.className = `etat etat--${classe}`;
}

function connecter() {
  deconnecter();
  const adresse = adresseWebSocket(reglages.url, reglages.token);
  if (!adresse) {
    majEtat('Adresse invalide', 'hors');
    return;
  }
  majEtat('Connexion…', 'attente');

  let socket;
  try {
    socket = new WebSocket(adresse);
  } catch (erreur) {
    programmerReconnexion();
    return;
  }
  etat.socket = socket;

  socket.onopen = () => {
    etat.reconnexion = RECONNEXION_MIN;
    majEtat('En direct', 'direct');
    elements.messageReglages.textContent = '';
  };

  socket.onmessage = (evenement) => {
    let snapshot;
    try {
      snapshot = JSON.parse(evenement.data);
    } catch (erreur) {
      return;
    }
    appliquerSnapshot(snapshot);
  };

  socket.onclose = (evenement) => {
    etat.socket = null;
    // 1008 = refus applicatif : reessayer en boucle avec le meme jeton est inutile.
    if (evenement.code === 1008) {
      majEtat('Jeton refuse', 'hors');
      elements.messageReglages.textContent =
        "Jeton refuse par l'agent. Relancez « overmlay pair » pour en obtenir un valide.";
      ouvrirPanneau(elements.panneauReglages, elements.btnReglages, true);
      return;
    }
    majEtat('Hors ligne', 'hors');
    programmerReconnexion();
  };

  socket.onerror = () => socket.close();
}

function deconnecter() {
  if (etat.minuteur) {
    clearTimeout(etat.minuteur);
    etat.minuteur = null;
  }
  if (etat.socket) {
    etat.socket.onclose = null;
    etat.socket.close();
    etat.socket = null;
  }
}

function programmerReconnexion() {
  if (etat.minuteur) return;
  const delai = etat.reconnexion;
  // Recul exponentiel : un telephone qui sort du reseau ne doit pas marteler l'agent.
  etat.reconnexion = Math.min(etat.reconnexion * 2, RECONNEXION_MAX);
  etat.minuteur = setTimeout(() => {
    etat.minuteur = null;
    connecter();
  }, delai);
}

// --- Rendu -----------------------------------------------------------------

function appliquerSnapshot(snapshot) {
  const mesures = snapshot.readings || [];
  elements.hote.textContent = snapshot.host || '—';
  elements.vide.hidden = mesures.length > 0;

  let catalogueModifie = false;
  for (const mesure of mesures) {
    if (!etat.connuesMesures.has(mesure.key)) catalogueModifie = true;
    etat.connuesMesures.set(mesure.key, mesure);
    if (mesure.value !== null && mesure.value !== undefined) {
      const serie = etat.historique.get(mesure.key) || [];
      serie.push(mesure.value);
      if (serie.length > POINTS_COURBE) serie.shift();
      etat.historique.set(mesure.key, serie);
    }
  }
  if (catalogueModifie) construireListeMesures();

  const visibles = mesures.filter((mesure) => !etat.masques.has(mesure.key));
  const signature = visibles.map((mesure) => mesure.key).join('|');
  if (signature !== etat.clesRendues) {
    construireTuiles(visibles);
    etat.clesRendues = signature;
  }
  for (const mesure of visibles) majTuile(mesure);
}

function construireTuiles(mesures) {
  elements.tableau.textContent = '';
  etat.tuiles.clear();

  const parGroupe = new Map();
  for (const mesure of mesures) {
    if (!parGroupe.has(mesure.group)) parGroupe.set(mesure.group, []);
    parGroupe.get(mesure.group).push(mesure);
  }

  const groupesTries = [...parGroupe.keys()].sort((a, b) => {
    const rangA = ORDRE_GROUPES.indexOf(a);
    const rangB = ORDRE_GROUPES.indexOf(b);
    return (rangA < 0 ? 99 : rangA) - (rangB < 0 ? 99 : rangB);
  });

  for (const groupe of groupesTries) {
    const section = document.createElement('section');
    section.className = 'groupe';
    const titre = document.createElement('h2');
    titre.textContent = TITRES_GROUPES[groupe] || groupe;
    section.appendChild(titre);

    const grille = document.createElement('div');
    grille.className = 'grille';
    for (const mesure of parGroupe.get(groupe)) {
      grille.appendChild(creerTuile(mesure));
    }
    section.appendChild(grille);
    elements.tableau.appendChild(section);
  }
}

function creerTuile(mesure) {
  const tuile = document.createElement('article');
  tuile.className = 'tuile';

  const titre = document.createElement('div');
  titre.className = 'tuile__titre';
  titre.textContent = mesure.label;
  titre.title = mesure.key;

  const valeur = document.createElement('div');
  valeur.className = 'tuile__valeur';

  tuile.append(titre, valeur);

  const refs = { valeur, barre: null, courbe: null };

  if (mesure.gauge) {
    const jauge = document.createElement('div');
    jauge.className = 'jauge';
    const barre = document.createElement('div');
    barre.className = 'jauge__barre';
    jauge.appendChild(barre);
    tuile.appendChild(jauge);
    refs.barre = barre;
  } else {
    const courbe = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    courbe.setAttribute('class', 'courbe');
    courbe.setAttribute('viewBox', '0 0 100 28');
    courbe.setAttribute('preserveAspectRatio', 'none');
    courbe.setAttribute('aria-hidden', 'true');
    const trace = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
    trace.setAttribute('fill', 'none');
    trace.setAttribute('stroke', 'currentColor');
    trace.setAttribute('stroke-width', '1.5');
    trace.setAttribute('vector-effect', 'non-scaling-stroke');
    courbe.appendChild(trace);
    tuile.appendChild(courbe);
    refs.courbe = trace;
  }

  etat.tuiles.set(mesure.key, refs);
  return tuile;
}

function majTuile(mesure) {
  const refs = etat.tuiles.get(mesure.key);
  if (!refs) return;

  if (mesure.value === null || mesure.value === undefined) {
    refs.valeur.className = 'tuile__valeur tuile__absent';
    refs.valeur.textContent = '—';
  } else {
    refs.valeur.className = 'tuile__valeur';
    refs.valeur.textContent = formaterNombre(mesure.value);
    const unite = document.createElement('span');
    unite.className = 'tuile__unite';
    unite.textContent = mesure.unit;
    refs.valeur.appendChild(unite);
  }

  if (refs.barre) majJauge(refs.barre, mesure);
  if (refs.courbe) majCourbe(refs.courbe, etat.historique.get(mesure.key) || []);
}

function formaterNombre(valeur) {
  const absolu = Math.abs(valeur);
  if (absolu >= 10000) return Math.round(valeur).toLocaleString('fr-FR');
  if (absolu >= 100) return valeur.toFixed(0);
  if (absolu >= 10) return valeur.toFixed(1);
  return valeur.toFixed(absolu < 1 ? 2 : 1);
}

function majJauge(barre, mesure) {
  if (mesure.value === null || mesure.value === undefined) {
    barre.style.width = '0%';
    return;
  }
  const bas = typeof mesure.min === 'number' ? mesure.min : 0;
  const haut = typeof mesure.max === 'number' ? mesure.max : 100;
  const etendue = haut - bas;
  const ratio = etendue > 0 ? (mesure.value - bas) / etendue : 0;
  const pourcent = Math.max(0, Math.min(1, ratio)) * 100;
  barre.style.width = `${pourcent.toFixed(1)}%`;

  let classe = 'jauge__barre';
  if (mesure.kind === 'temperature' || mesure.kind === 'load' || mesure.kind === 'power') {
    if (pourcent >= 85) classe += ' jauge__barre--chaud';
    else if (pourcent >= 65) classe += ' jauge__barre--tiede';
    else classe += ' jauge__barre--ok';
  }
  barre.className = classe;
}

function majCourbe(trace, serie) {
  if (serie.length < 2) {
    trace.setAttribute('points', '');
    return;
  }
  const bas = Math.min(...serie);
  const haut = Math.max(...serie);
  const etendue = haut - bas || 1;
  const pas = 100 / (serie.length - 1);
  const points = serie
    .map((valeur, index) => {
      const y = 26 - ((valeur - bas) / etendue) * 24;
      return `${(index * pas).toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
  trace.setAttribute('points', points);
}

// --- Choix des mesures -----------------------------------------------------

function construireListeMesures() {
  elements.listeMesures.textContent = '';
  const mesures = [...etat.connuesMesures.values()].sort((a, b) => {
    const rangA = ORDRE_GROUPES.indexOf(a.group);
    const rangB = ORDRE_GROUPES.indexOf(b.group);
    if (rangA !== rangB) return (rangA < 0 ? 99 : rangA) - (rangB < 0 ? 99 : rangB);
    return a.label.localeCompare(b.label, 'fr');
  });

  for (const mesure of mesures) {
    const etiquette = document.createElement('label');
    const case_ = document.createElement('input');
    case_.type = 'checkbox';
    case_.checked = !etat.masques.has(mesure.key);
    case_.addEventListener('change', () => {
      if (case_.checked) etat.masques.delete(mesure.key);
      else etat.masques.add(mesure.key);
      ecrireJson(CLE_MASQUES, [...etat.masques]);
      etat.clesRendues = '';
    });
    const texte = document.createElement('span');
    texte.textContent = mesure.label;
    texte.title = mesure.key;
    etiquette.append(case_, texte);
    elements.listeMesures.appendChild(etiquette);
  }
}

function basculerToutes(afficher) {
  if (afficher) etat.masques.clear();
  else for (const cle of etat.connuesMesures.keys()) etat.masques.add(cle);
  ecrireJson(CLE_MASQUES, [...etat.masques]);
  etat.clesRendues = '';
  construireListeMesures();
}

// --- Veille ecran ----------------------------------------------------------

async function majVerrouEcran() {
  if (!('wakeLock' in navigator)) return;
  if (reglages.veille && !etat.verrouEcran) {
    try {
      etat.verrouEcran = await navigator.wakeLock.request('screen');
      etat.verrouEcran.addEventListener('release', () => {
        etat.verrouEcran = null;
      });
    } catch (erreur) {
      /* refuse par le navigateur (batterie faible, onglet masque) */
    }
  } else if (!reglages.veille && etat.verrouEcran) {
    etat.verrouEcran.release();
    etat.verrouEcran = null;
  }
}

// --- Interface -------------------------------------------------------------

function ouvrirPanneau(panneau, bouton, forcer) {
  const ouvrir = forcer !== undefined ? forcer : panneau.hidden;
  panneau.hidden = !ouvrir;
  bouton.setAttribute('aria-expanded', String(ouvrir));
}

elements.btnReglages.addEventListener('click', () => {
  elements.panneauAffichage.hidden = true;
  elements.btnAffichage.setAttribute('aria-expanded', 'false');
  ouvrirPanneau(elements.panneauReglages, elements.btnReglages);
});

elements.btnAffichage.addEventListener('click', () => {
  elements.panneauReglages.hidden = true;
  elements.btnReglages.setAttribute('aria-expanded', 'false');
  ouvrirPanneau(elements.panneauAffichage, elements.btnAffichage);
});

elements.btnConnecter.addEventListener('click', () => {
  reglages = {
    url: elements.champUrl.value.trim() || window.location.origin,
    token: elements.champToken.value.trim(),
    veille: elements.champVeille.checked,
  };
  ecrireJson(CLE_REGLAGES, reglages);
  etat.reconnexion = RECONNEXION_MIN;
  elements.messageReglages.textContent = '';
  majVerrouEcran();
  connecter();
  ouvrirPanneau(elements.panneauReglages, elements.btnReglages, false);
});

elements.btnOublier.addEventListener('click', () => {
  deconnecter();
  try {
    localStorage.removeItem(CLE_REGLAGES);
    localStorage.removeItem(CLE_MASQUES);
  } catch (erreur) {
    /* ignore */
  }
  reglages = { url: window.location.origin, token: '', veille: false };
  etat.masques.clear();
  remplirFormulaire();
  majEtat('Hors ligne', 'hors');
  elements.messageReglages.textContent = 'Reglages effaces.';
});

elements.btnTout.addEventListener('click', () => basculerToutes(true));
elements.btnRien.addEventListener('click', () => basculerToutes(false));

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  majVerrouEcran();
  // Un telephone qui sort de veille a souvent perdu la socket sans evenement.
  if (!etat.socket || etat.socket.readyState > WebSocket.OPEN) {
    etat.reconnexion = RECONNEXION_MIN;
    connecter();
  }
});

function remplirFormulaire() {
  elements.champUrl.value = reglages.url;
  elements.champToken.value = reglages.token;
  elements.champVeille.checked = reglages.veille;
}

if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/app/sw.js').catch(() => {
      /* hors PWA : sans importance */
    });
  });
}

remplirFormulaire();
majVerrouEcran();
connecter();
