# Overmlay

Overlay de monitoring materiel pour le PC, avec une application mobile compagnon.

Affiche en temps reel les images par seconde, les temperatures, les charges CPU et
GPU, la consommation electrique du processeur et de la carte graphique, la memoire
et les vitesses de ventilateur — a l'ecran par-dessus le jeu, et sur le telephone. Chaque mesure peut etre montree ou masquee a la demande, et
l'overlay entier s'ouvre et se ferme par un raccourci clavier.

```
┌──────────────────────────────────────────────┐
│  Agent Overmlay (Python, sur le PC)          │
│                                              │
│  capteurs ──► hub ──┬──► overlay Qt          │   ← a l'ecran, par-dessus le jeu
│  (hwmon, NVML,      │                        │
│   LHM, psutil)      └──► serveur HTTP/WS ────┼──► telephone (PWA)
│  + source FPS                                │     sur le reseau local
│  (PresentMon / MangoHud / API)               │
└──────────────────────────────────────────────┘
```

Les deux sorties lisent le meme flux de mesures : ce que montre l'overlay et ce que
montre le telephone sont toujours coherents.


## Installation

Python 3.10 ou plus recent.

```bash
git clone https://github.com/feanor91/overmlay-perf
cd overmlay-perf
pip install ".[all]"
```

Les extras se choisissent separement si besoin :

| Extra | Contenu | Necessaire pour |
| --- | --- | --- |
| *(aucun)* | psutil, platformdirs | lire les capteurs, `overmlay sensors` |
| `server` | FastAPI, uvicorn, qrcode | l'application mobile |
| `overlay` | PySide6, pynput | l'affichage a l'ecran et le raccourci global |
| `all` | les deux | l'usage courant |


## Demarrage rapide

```bash
overmlay sensors      # ce que votre machine expose reellement
overmlay run          # overlay + serveur mobile
overmlay pair         # QR code a scanner depuis le telephone
```

`overmlay run` lance l'overlay et le serveur. `overmlay serve` ne lance que le
serveur (pratique sur une machine sans session graphique), `overmlay overlay` ne
lance que l'affichage local.

Aucun materiel sous la main ? `mock = true` dans la section `[general]` remplace
tous les capteurs par des valeurs simulees, de quoi regler l'affichage tranquillement.


## L'application mobile

Il n'y a rien a installer depuis un magasin d'applications : l'agent sert lui-meme
une application web installable (PWA).

1. Sur le PC : `overmlay pair`, qui affiche une adresse et un QR code.
2. Sur le telephone, connecte au **meme reseau local**, scannez le QR code ou
   saisissez l'adresse.
3. « Ajouter a l'ecran d'accueil » depuis le menu du navigateur : l'application
   s'ouvre alors en plein ecran, avec sa propre icone.

Le jeton d'acces voyage dans le fragment de l'URL (`#token=…`), jamais dans la
chaine de requete : il n'apparait donc ni dans les journaux serveur ni dans
l'en-tete `Referer`. Il est ensuite conserve sur le telephone.

L'application affiche les mesures groupees par materiel, avec une jauge coloree
quand l'echelle a un sens et une courbe d'historique sinon. Le bouton **Mesures**
ouvre la liste complete : decochez ce que vous ne voulez pas voir, le choix est
conserve sur ce telephone. L'option « Garder l'ecran allume » evite la mise en
veille pendant une session de jeu.

L'application retient **deux adresses** : celle du reseau local et, si vous en
configurez une, celle joignable depuis l'exterieur. Elle essaie la locale en
premier — sur place, elle evite le detour par Internet — puis bascule sur la
distante en une seconde si elle ne repond pas. L'etat affiche laquelle est en
service (« En direct · local » ou « En direct · distant »).

La connexion se retablit toute seule apres une coupure Wi-Fi ou une sortie de
veille, avec un recul exponentiel pour ne pas marteler l'agent.


## Acces depuis un autre reseau

Par defaut, l'agent n'est joignable que sur le reseau local. Pour consulter ses
mesures depuis l'exterieur, il faut le rendre accessible — et c'est la que se joue
la securite de l'installation, puisque le jeton donne acces a l'etat detaille de la
machine.

**La bonne approche n'est pas d'ouvrir un port sur la box.** Un port ouvert est
balaye par des robots en quelques heures, votre adresse IP change, et beaucoup
d'abonnements passent par du CGNAT ou la redirection de port ne fonctionne meme
pas. Un tunnel sortant evite les trois problemes a la fois et apporte un
certificat HTTPS valide, sans lequel le telephone refuse d'installer
l'application.

### Tailscale — recommande

Le telephone et le PC rejoignent un reseau prive chiffre. Rien n'est expose sur
Internet, et `tailscale serve` fournit un vrai certificat HTTPS.

```bash
# Sur le PC, apres « tailscale up »
tailscale serve --bg 8777
tailscale status   # donne le nom complet de la machine
```

```toml
[server]
host = "127.0.0.1"          # l'agent n'ecoute que pour le tunnel
public_url = "https://mon-pc.tail1234.ts.net"
behind_proxy = true
```

Installez ensuite Tailscale sur le telephone : l'adresse fonctionne de partout,
sans rien ouvrir sur la box.

### Cloudflare Tunnel

Utile pour une URL accessible sans installer de client sur le telephone.

```bash
cloudflared tunnel --url http://127.0.0.1:8777
```

La commande affiche une URL en `https://….trycloudflare.com`, a reporter dans
`public_url`. Pour un usage durable, un tunnel nomme sur votre propre domaine est
preferable, et **Cloudflare Access** ajoute une authentification devant l'agent —
une seconde serrure en plus du jeton.

### Redirection de port — a eviter

Si vous y tenez malgre tout, ne le faites jamais en HTTP simple : le jeton et
toute la telemetrie circuleraient en clair sur chaque reseau traverse. Il faut un
certificat valide (un `tls_cert` auto-signe ne convient pas : les navigateurs
refusent d'installer une application depuis une origine non approuvee) et,
idealement, un port non standard. Overmlay vous avertit au demarrage si
`public_url` est en HTTP.

### Ce que fait Overmlay de son cote

- **Verrouillage anti-force brute.** Apres 10 echecs d'authentification en cinq
  minutes, l'adresse fautive est bloquee pendant cinq minutes (`max_auth_failures`
  et `auth_lockout_seconds`). Le verrou porte sur l'adresse, pas sur le jeton :
  une fois joignable depuis Internet, c'est ce qui rend une attaque par
  enumeration sans interet pratique.
- **Identification derriere le tunnel.** Avec `behind_proxy = true`, l'agent lit
  `X-Forwarded-For` pour distinguer les clients — sans quoi ils partageraient tous
  l'adresse du tunnel et un seul attaquant verrouillerait tout le monde. Cette
  en-tete n'est **jamais** lue sans cette option : elle est triviale a forger, et
  la croire permettrait d'echapper au verrou en changeant de valeur a chaque essai.
- **Rotation du jeton.** `overmlay pair --rotate` en genere un nouveau et
  invalide les telephones deja appaires. A faire au moindre doute.


## Ce qui est mesure, et comment

### Images par seconde

Overmlay ne s'injecte dans aucun jeu. Trois sources, selectionnees par
`[fps] mode` :

| Mode | Plateforme | Fonctionnement |
| --- | --- | --- |
| `presentmon` | Windows | [PresentMon](https://github.com/GameTechDev/PresentMon) (Intel, open source) lit les traces ETW de presentation DXGI/D3D/Vulkan. Mesure tous les jeux, y compris en plein ecran exclusif. **Demande les droits administrateur.** |
| `mangohud` | Linux | Suit les journaux CSV de [MangoHud](https://github.com/flightlessmango/MangoHud), deja utilise comme couche Vulkan/OpenGL. |
| `push` | toutes | Votre programme publie ses trames sur `POST /api/fps/frame`. Voir `examples/push_fps.py`. |
| `auto` | toutes | PresentMon s'il est present, sinon MangoHud. |
| `off` | toutes | Desactive la mesure du FPS. |

Cote MangoHud, lancez le jeu en journalisant :

```bash
MANGOHUD_CONFIG=output_folder=~/.local/share/overmlay/mangohud,autostart_log=1 mangohud %command%
```

Outre le FPS moyen, Overmlay publie le temps de trame et les centiles bas
(**1 % low** et **0,1 % low**), c'est-a-dire l'inverse des 99e et 99,9e centiles de
duree de trame. Ce sont eux qui decrivent les saccades que la moyenne masque. Les
interruptions de flux (alt-tab, ecran de chargement) sont ecartees pour ne pas
fausser durablement ces centiles.

### Temperatures, ventilateurs, charges

| Plateforme | Source | Couverture |
| --- | --- | --- |
| Linux | `/sys/class/hwmon` | temperatures CPU/NVMe, vitesses de rotation, rapport PWM, puissances |
| Linux | powercap / RAPL | **consommation du processeur** : boitier, coeurs, memoire |
| Linux | sysfs `amdgpu` | GPU AMD : charge, VRAM, temperature, ventilateur, **consommation** |
| Windows | LibreHardwareMonitor | temperatures, ventilateurs, consommations, frequences |
| Toutes | NVML / `nvidia-smi` | GPU NVIDIA : charge, temperature, VRAM, ventilateur, **consommation**, frequences |
| Toutes | psutil | charge CPU (globale ou par coeur), frequence, RAM, swap, debits disque et reseau |

**Sous Windows, une etape manuelle est indispensable.** Le systeme n'expose aucune
API publique pour les temperatures et les ventilateurs : il faut un pilote en mode
noyau. Installez [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
lancez-le **en administrateur**, puis activez `Options > Remote Web Server > Run`.
Overmlay interroge alors son serveur interne sur `http://127.0.0.1:8085/data.json`.
Sans lui, vous aurez la charge CPU, la memoire et le GPU NVIDIA, mais ni les
temperatures de la carte mere ni les vitesses de ventilateur.

### Consommation electrique

Les deux composants qui pesent dans la facture et dans la chaleur du boitier ont
chacun une cle dediee, affichee par defaut dans l'overlay :

| Cle | Composant | Source |
| --- | --- | --- |
| `cpu.power` | processeur | powercap/RAPL sous Linux, LibreHardwareMonitor sous Windows |
| `gpu.N.power` | carte graphique | NVML/`nvidia-smi`, sysfs `amdgpu`, LibreHardwareMonitor |

Sous Linux, `cpu.power` se decline en `cpu.power.core`, `cpu.power.uncore` et
`cpu.power.dram` quand le processeur expose ces sous-domaines — ce sont des parts
du total, jamais des supplements. Sur une machine bi-socket, `cpu.power` est la
somme des boitiers, chacun restant disponible en `cpu.package.N.power`.

**RAPL compte de l'energie, pas une puissance.** Overmlay deduit les watts de la
variation du compteur entre deux cycles : la toute premiere lecture ne produit donc
rien, et `overmlay sensors` echantillonne deux fois pour cette raison.

**Ces compteurs sont souvent reserves a root.** Depuis la CVE-2020-8694 — une
mesure fine de la consommation permet des attaques par canal auxiliaire — la
plupart des distributions restreignent leur lecture. Overmlay se desactive alors
proprement en expliquant la marche a suivre plutot que d'afficher un vide. Pour les
ouvrir :

```bash
sudo chmod a+r /sys/class/powercap/*/energy_uj   # a refaire au redemarrage
```

Pour que ce soit permanent, une regle udev est preferable a un `chmod` manuel.

**La consommation de la carte graphique** sort sous la meme cle `gpu.N.power`
chez NVIDIA et chez AMD, avec la limite de la carte (`power limit` / `power1_cap`)
comme pleine echelle de la jauge. Une seule ligne de configuration couvre donc les
deux fabricants. Il en va de meme pour `gpu.N.load`, `gpu.N.temp`, `gpu.N.fan` et
`gpu.N.vram.used`. Seule exception : un GPU AMD **sous Windows** passe par
LibreHardwareMonitor et garde des cles `lhm.*` propres a la machine, que
`overmlay sensors` vous donnera. Il en va de meme pour `cpu.power` sous Windows :
LibreHardwareMonitor le publie sous une cle `lhm.*` qui contient le modele du
processeur.

Quand plusieurs sources publient la meme cle, la plus precise l'emporte, et les
temperatures psutil sont automatiquement desactivees des qu'une source dediee est
disponible. Sous Linux, le sous-repertoire hwmon d'une carte AMD est lu par le
backend `amdgpu` puis ignore par le backend hwmon generique : sa consommation
n'apparait donc qu'une fois. Sur une machine hybride, les cartes AMD sont
numerotees a la suite des cartes NVIDIA, pour que les deux ne se disputent pas
`gpu.0`.

`overmlay sensors` liste les cles reellement disponibles sur votre machine ; ce
sont elles qu'on met dans `[overlay] metrics`.


## Configuration

```bash
overmlay config --init   # cree le fichier a partir du modele commente
overmlay config --path   # affiche son emplacement
```

Le fichier vit dans le repertoire de configuration de l'utilisateur
(`~/.config/overmlay/config.toml` sous Linux,
`%LOCALAPPDATA%\overmlay\config.toml` sous Windows) ; `--config` accepte un autre
chemin. Toute option omise garde sa valeur par defaut, et une valeur invalide est
refusee au demarrage avec un message explicite plutot qu'en cours de route.

Le modele complet et commente se trouve dans
[`src/overmlay/data/config.example.toml`](src/overmlay/data/config.example.toml).
Les reglages les plus utiles :

```toml
[general]
poll_interval = 1.0        # cadence d'echantillonnage, en secondes

[overlay]
position = "top-left"      # ou top-right, bottom-left, bottom-right
opacity = 0.85
columns = 2                # repartir les lignes sur plusieurs colonnes
hotkey = "<ctrl>+<alt>+o"  # raccourci global d'affichage
visible_at_start = true
metrics = ["fps.current", "cpu.load", "cpu.power", "gpu.0.temp", "gpu.0.power", "fan.*"]

[server]
host = "0.0.0.0"           # 127.0.0.1 pour interdire l'acces reseau
port = 8777
```

Dans `metrics`, un `*` final agit comme un prefixe : `fan.*` prend tous les
ventilateurs, `gpu.0.*` tout ce qui concerne le premier GPU. L'ordre de la liste
est l'ordre d'affichage.


## API

Toutes les routes `/api/*` (et le WebSocket) exigent le jeton, presente au choix
dans `Authorization: Bearer …`, dans l'en-tete `X-Overmlay-Token` ou dans le
parametre d'URL `token` — ce dernier etant le seul moyen d'authentifier un
WebSocket depuis un navigateur.

| Route | Role |
| --- | --- |
| `GET /api/health` | Sonde publique : version, machine, authentification requise |
| `GET /api/metrics` | Dernier instantane. `?keys=cpu.load,gpu.*` pour filtrer |
| `GET /api/sensors` | Backends actifs et catalogue des mesures disponibles |
| `GET /api/history` | Jusqu'a 1000 instantanes passes, pour tracer des courbes |
| `GET /api/fps` | Detail du compteur d'images (moyenne, centiles bas, application) |
| `POST /api/fps/frame` | Publier une trame : `{"frame_time_ms": 8.33}` |
| `WS /ws` | Flux temps reel, un message JSON par cycle de collecte |

```bash
curl -H "Authorization: Bearer $JETON" \
     "http://127.0.0.1:8777/api/metrics?keys=cpu.load,gpu.0.temp"
```


## Securite

Le serveur ecoute par defaut sur `0.0.0.0` pour que le telephone puisse le joindre.
Il publie l'etat detaille de la machine : traitez le jeton comme un mot de passe.

- Un jeton aleatoire est genere au premier lancement et conserve dans le repertoire
  de donnees de l'utilisateur, en lecture seule proprietaire.
- Les comparaisons de jeton se font a temps constant.
- Une adresse est verrouillee apres des echecs d'authentification repetes.
- Pour un usage strictement local, mettez `host = "127.0.0.1"`.
- Sur le reseau local, le trafic est en HTTP simple : c'est adapte a un reseau
  domestique de confiance. **Pour un acces depuis l'exterieur, passez par un
  tunnel chiffre** — voir la section dediee plus haut.


## Limites connues

- **Plein ecran exclusif.** Un jeu DirectX en plein ecran exclusif dessine
  directement sur le balayage ecran et masque toute fenetre, overlay compris. En
  plein ecran fenetre ou sans bordure — le mode par defaut de la plupart des jeux
  recents — l'overlay s'affiche normalement. Le telephone reste de toute facon une
  sortie utilisable, et c'est meme sa principale raison d'etre.
- **Wayland.** Le positionnement absolu des fenetres et les raccourcis clavier
  globaux y sont restreints par conception. L'overlay fonctionne sous X11 ou XWayland ;
  sous Wayland pur, le raccourci peut rester inactif — Overmlay le signale au
  demarrage plutot que d'echouer en silence.
- **PresentMon** demande les droits administrateur.
- **Ventilateurs a zero.** Une vitesse de 0 RPM est generalement un connecteur
  libre sur la carte mere, pas une panne.


## Developpement

```bash
pip install -e ".[dev,overlay]"
python -m pytest -q                  # 236 tests
python -m ruff check src tests tools
python tools/make_icons.py           # regenere les icones de la PWA
```

Les tests de l'overlay demandent PySide6 et sont sautes automatiquement s'il est
absent ; en environnement sans affichage, `QT_QPA_PLATFORM=offscreen` suffit. La
logique de connexion de l'application mobile est testee en chargeant `app.js` dans
un contexte Node muni d'un DOM et d'un WebSocket factices (`tests/webapp/`) ; ces
tests sont sautes si Node n'est pas installe.

Organisation du code :

| Chemin | Role |
| --- | --- |
| `src/overmlay/models.py` | `Reading` et `Snapshot`, le vocabulaire partage par tout le reste |
| `src/overmlay/sensors/` | Un module par source materielle, plus la detection automatique |
| `src/overmlay/fps/` | Calcul des metriques de fluidite et sources de trames |
| `src/overmlay/hub.py` | Boucle d'echantillonnage et diffusion aux abonnes |
| `src/overmlay/server/` | API HTTP/WebSocket, jeton, appairage |
| `src/overmlay/webapp/` | L'application mobile (HTML/CSS/JS, sans dependance) |
| `src/overmlay/overlay/` | Fenetre Qt et raccourci global |

Ajouter une source de capteurs revient a implementer `SensorBackend` (`available()`
et `read()`) puis a la declarer dans `detect_backends()` : l'overlay, l'API et
l'application mobile la reprennent sans modification.


## Licence

MIT — voir [LICENSE](LICENSE).
