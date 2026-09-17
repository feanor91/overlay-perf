# Overlay

Monitoring materiel du PC affiche par-dessus le jeu, avec une application
mobile compagnon.

Affiche en temps reel les images par seconde, les temperatures, les charges CPU et
GPU, la consommation electrique du processeur et de la carte graphique, la memoire
et les vitesses de ventilateur — a l'ecran par-dessus le jeu, et sur le telephone. Chaque mesure peut etre montree ou masquee a la demande, et
l'overlay entier s'ouvre et se ferme par un raccourci clavier. Une icone dans la
zone de notification donne acces a l'appairage du telephone et permet de
quitter sans repasser par un terminal ; Ctrl-C fonctionne aussi.

```
┌──────────────────────────────────────────────┐
│  Agent Overlay (Python, sur le PC)           │
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


## Version Windows en un seul executable (C#/.NET)

Le dossier [`dotnet/`](dotnet/README.md) contient une reecriture complete de l'agent en
C#/.NET 8 : un unique `OverlayPerf.exe`, sans Python a installer, qui **demande les droits
administrateur au lancement** (invite UAC), integre LibreHardwareMonitor (plus rien a lancer
a part pour les temperatures et ventilateurs), tient un **journal quotidien** dans
`%LOCALAPPDATA%\overlay\logs` et garde l'icone de zone de notification, le serveur mobile,
la configuration et le jeton de l'agent Python. Voir [dotnet/README.md](dotnet/README.md).
L'agent Python reste la version multiplateforme (Linux : MangoHud, hwmon, RAPL).

## Installation

Python 3.10 ou plus recent.

```bash
git clone https://github.com/feanor91/overlay-perf
cd overlay-perf
python -m venv .venv
source .venv/bin/activate        # Linux/macOS ; sous Windows : .venv\Scripts\activate
pip install ".[all]"
```

Un environnement virtuel n'est pas strictement necessaire, mais evite un piege
frequent sous Windows (voir ci-dessous) et empeche les dependances d'Overlay de se
melanger a celles d'autres projets Python.

Les extras se choisissent separement si besoin :

| Extra | Contenu | Necessaire pour |
| --- | --- | --- |
| *(aucun)* | psutil, platformdirs | lire les capteurs, `overlay sensors` |
| `server` | FastAPI, uvicorn, qrcode | l'application mobile |
| `overlay` | PySide6, pynput | l'affichage a l'ecran et le raccourci global |
| `all` | les deux | l'usage courant |

**Sous Windows, sans environnement virtuel**, `pip install` place `overlay.exe`
dans `%APPDATA%\Python\PythonXY\Scripts` — un dossier absent du `PATH` par
defaut. La commande `overlay` reste alors introuvable juste apres
l'installation (`CommandNotFoundException` sous PowerShell), sans rien
d'anormal a l'installation elle-meme. Deux solutions, sans rien reinstaller :

- lancer `python -m overlay ...` a la place de `overlay ...` : fonctionne
  toujours, meme sans venv, puisqu'il ne depend pas du `PATH` ;
- ajouter ce dossier au `PATH` de l'utilisateur — son chemin exact est donne
  par l'avertissement de pip au moment de l'installation — puis rouvrir le
  terminal pour que le changement prenne effet.


## Demarrage rapide

```bash
overlay sensors      # ce que votre machine expose reellement
overlay               # overlay + serveur mobile : equivalent a « overlay run »
overlay pair          # QR code a scanner depuis le telephone
```

**`overlay` sans rien derriere lance l'overlay et le serveur** — c'est le
comportement par defaut, `overlay run` est juste la forme explicite du meme
appel. `overlay serve` ne lance que le serveur (pratique sur une machine sans
session graphique), `overlay overlay` ne lance que l'affichage local.

Aucun materiel sous la main ? `mock = true` dans la section `[general]` remplace
tous les capteurs par des valeurs simulees, de quoi regler l'affichage tranquillement.


## L'application mobile

Il n'y a rien a installer depuis un magasin d'applications : l'agent sert lui-meme
une application web installable (PWA).

1. Sur le PC : `overlay pair`, qui affiche une adresse et un QR code.
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

**Avec l'overlay a l'ecran (`overlay run` ou `overlay overlay`), inutile de
repasser par `overlay pair` dans un terminal** : une icone apparait dans la
zone de notification des le demarrage, avec un menu « Appairer un
telephone… » qui affiche la meme adresse et le meme QR code, en fenetre.
Desactivable via `[overlay] tray_icon = false`.


## Quitter proprement

- **Ctrl-C** dans le terminal fonctionne, overlay affiche ou non.
- **Un raccourci global** (`<ctrl>+<alt>+q` par defaut, configurable via
  `[overlay] hotkey_quit`) quitte sans avoir a revenir au terminal.
- **L'icone de zone de notification** propose « Quitter » dans son menu.

Dans les trois cas, Overlay arrete proprement le serveur, la collecte et la
source FPS avant de sortir.

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
idealement, un port non standard. Overlay vous avertit au demarrage si
`public_url` est en HTTP.

### Ce que fait Overlay de son cote

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
- **Rotation du jeton.** `overlay pair --rotate` en genere un nouveau et
  invalide les telephones deja appaires. A faire au moindre doute.


## Ce qui est mesure, et comment

### Images par seconde

Overlay ne s'injecte dans aucun jeu. Trois sources, selectionnees par
`[fps] mode` :

| Mode | Plateforme | Fonctionnement |
| --- | --- | --- |
| `presentmon` | Windows | [PresentMon](https://github.com/GameTechDev/PresentMon) (Intel, open source) lit les traces ETW de presentation DXGI/D3D/Vulkan. Mesure tous les jeux, y compris en plein ecran exclusif. **Demande les droits administrateur.** |
| `mangohud` | Linux | Suit les journaux CSV de [MangoHud](https://github.com/flightlessmango/MangoHud), deja utilise comme couche Vulkan/OpenGL. |
| `push` | toutes | Votre programme publie ses trames sur `POST /api/fps/frame`. Voir `examples/push_fps.py`. |
| `auto` | toutes | PresentMon s'il est present, sinon MangoHud. |
| `off` | toutes | Desactive la mesure du FPS. |

**Attention au nom : deux outils differents s'appellent tous les deux
`PresentMon.exe`.** Le depot [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon)
publie a la fois l'**outil console** attendu ici (le binaire de release porte un
nom versionne, par exemple `PresentMon-2.3.1-x64.exe`) et une **application
graphique** distincte, « PresentMon Capture » (fenetre avec reglages, hotkeys,
auto-target), qui se lance elle sous le nom `PresentMon.exe` — exactement celui
qu'Overlay recherche sur le `PATH`. Si les deux sont installees, Overlay peut
trouver et lancer la mauvaise, qui ignore silencieusement les arguments qu'on lui
passe et se contente d'ouvrir sa fenetre : aucune trame ne remonte jamais, sans
la moindre erreur. Pour lever toute ambiguite, indiquez le chemin exact de
l'outil console :

```toml
[fps]
presentmon_path = "C:/Chemin/Vers/PresentMon-2.3.1-x64.exe"
```

**Si ce chemin est errone (faute de frappe, dossier deplace, extraction
incomplete), Overlay le signale clairement au lieu de laisser passer une
trace Python brute :**

```
Attention : PresentMon introuvable au moment de le lancer : C:/Chemin/Vers/PresentMon-2.3.1-x64.exe
  Ce chemin n'existe pas ou n'est pas accessible (faute de frappe, dossier deplace,
  extraction incomplete). Verifiez-le, notamment dans [fps] presentmon_path si vous
  l'avez renseigne, ou videz ce reglage pour rechercher automatiquement sur le PATH.
```

Cote MangoHud, lancez le jeu en journalisant :

```bash
MANGOHUD_CONFIG=output_folder=~/.local/share/overlay/mangohud,autostart_log=1 mangohud %command%
```

Outre le FPS moyen, Overlay publie le temps de trame et les centiles bas
(**1 % low** et **0,1 % low**), c'est-a-dire l'inverse des 99e et 99,9e centiles de
duree de trame. Ce sont eux qui decrivent les saccades que la moyenne masque. Les
interruptions de flux (alt-tab, ecran de chargement) sont ecartees pour ne pas
fausser durablement ces centiles.

**En mode `auto` (le defaut), Overlay previent si aucune source n'est trouvee.**
Sous Windows sans PresentMon installe, ou sous Linux sans journalisation MangoHud
active, FPS/temps de trame/1 % low restaient auparavant vides sans le moindre
indice — meme en mode verbose. Au lancement de `overlay run`, `overlay serve` ou
`overlay overlay`, un avertissement adapte a la plateforme s'affiche desormais :

```
Attention : Aucune source de FPS trouvee : FPS, temps de trame et 1 % low resteront
absents.
  1. Installez PresentMon (https://github.com/GameTechDev/PresentMon).
  2. Verifiez qu'il est sur le PATH (« presentmon --version » doit repondre),
     ou indiquez son chemin dans [fps] presentmon_path.
  3. Lancez Overlay en administrateur : PresentMon en a besoin pour suivre les
     evenements de presentation.
```

Passer `[fps] mode` a `"off"` desactive completement le suivi FPS, y compris cet
avertissement.

**Sous Windows, Overlay verifie aussi les droits administrateur des que
PresentMon est trouve**, sans attendre 30 secondes ni un jeu lance : c'est la
cause la plus frequente, en pratique, de FPS/1 % low restant vides alors que
PresentMon est bien installe et bien identifie.

```
Attention : PresentMon a ete trouve (C:/.../PresentMon-2.3.1-x64.exe) mais Overlay
ne tourne pas en administrateur : PresentMon va demarrer sans erreur visible, mais
ne transmettra jamais aucune trame (FPS, temps de trame et 1 % low resteront a « — »).
  Fermez Overlay, puis relancez votre terminal via un clic droit -> « Executer en
  tant qu'administrateur ».
```

**Ce premier avertissement ne couvre que « rien trouve ».** Un executable trouve
et lance avec succes (le processus demarre normalement) peut malgre tout ne
jamais transmettre une seule trame — le cas du piege de nommage ci-dessus, ou
des droits administrateur manquants. Rien dans le cycle de vie du processus ne
le signale de lui-meme : Overlay verifie donc, 30 secondes apres le demarrage,
qu'une source trouvee a effectivement produit des trames. Si aucun jeu ne tourne
encore a ce moment-la, c'est normal et le message le precise ; si un jeu tourne
deja sans que rien ne remonte, il pointe directement vers les deux causes les
plus frequentes :

```
Attention : Source FPS « presentmon » demarree, mais aucune trame recue apres 30 s.
  Si aucun jeu n'est lance pour l'instant, c'est normal : rien a mesurer
  encore, ce message n'indique rien d'anormal. Si un jeu tourne deja :
  - « PresentMon.exe » designe deux outils differents publies par le meme
    projet : l'outil console attendu ici, et l'application graphique
    « PresentMon Capture » (fenetre avec reglages, hotkeys, auto-target)
    qui porte le meme nom de fichier mais ne produit pas le meme flux.
    Verifiez lequel est reellement installe sur le PATH, ou indiquez le
    chemin exact du console dans [fps] presentmon_path pour lever toute
    ambiguite (...) ;
  - PresentMon a besoin des droits administrateur : relancez Overlay en
    administrateur.
```

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
Overlay interroge alors son serveur interne sur `http://127.0.0.1:8085/data.json`.
Sans lui, vous aurez la charge CPU, la memoire et le GPU NVIDIA, mais ni les
temperatures de la carte mere ni les vitesses de ventilateur — et Overlay
l'annoncera clairement au demarrage (voir ci-dessous), plutot que de laisser
ces mesures manquer sans explication.

**Pour ne plus y penser a chaque session**, LibreHardwareMonitor retient ses
propres reglages d'une fois sur l'autre (verifie dans son code source, pas
suppose) : cochez, dans son menu **Options**, `Remote Web Server > Run`,
`Start Minimized` puis `Run on Windows Startup`. Il demarre ensuite deja
en administrateur et minimise a chaque ouverture de session Windows, serveur
actif, sans autre intervention. PresentMon, lui, n'a jamais besoin d'etre
lance a la main : Overlay le demarre et l'arrete lui-meme a chaque session.

**Overlay previent si LibreHardwareMonitor est injoignable.** Au lancement de
`overlay run`, `overlay serve` ou `overlay overlay`, si la source thermique
attendue manque, un avertissement clair s'affiche — la marche a suivre
ci-dessus, sans avoir a chercher dans les journaux :

```
Attention : LibreHardwareMonitor injoignable sur http://127.0.0.1:8085/data.json : les
temperatures, ventilateurs et consommations cote carte mere resteront absents.
  1. Lancez LibreHardwareMonitor en administrateur.
  2. Menu Options > Remote Web Server > Run.
  3. Pour ne plus y penser : Options > Start Minimized, puis Run on Windows Startup.
```

`overlay sensors` l'affiche egalement. Le message disparait des que la source
concernee redevient joignable ; le desactiver explicitement dans la
configuration (`sensors.disabled = ["lhm"]`) le fait taire, sans avertissement
ni requete reseau superflue.

### Consommation electrique

Les deux composants qui pesent dans la facture et dans la chaleur du boitier ont
chacun une cle dediee, affichee par defaut dans l'overlay :

| Cle | Composant | Source |
| --- | --- | --- |
| `cpu.temp` / `cpu.power` | processeur | hwmon+RAPL sous Linux, LibreHardwareMonitor sous Windows |
| `gpu.N.power` | carte graphique | NVML/`nvidia-smi`, sysfs `amdgpu`, LibreHardwareMonitor |

Sous Linux, `cpu.power` se decline en `cpu.power.core`, `cpu.power.uncore` et
`cpu.power.dram` quand le processeur expose ces sous-domaines — ce sont des parts
du total, jamais des supplements. Sur une machine bi-socket, `cpu.power` est la
somme des boitiers, chacun restant disponible en `cpu.package.N.power`.

**RAPL compte de l'energie, pas une puissance.** Overlay deduit les watts de la
variation du compteur entre deux cycles : la toute premiere lecture ne produit donc
rien, et `overlay sensors` echantillonne deux fois pour cette raison.

**Ces compteurs sont souvent reserves a root.** Depuis la CVE-2020-8694 — une
mesure fine de la consommation permet des attaques par canal auxiliaire — la
plupart des distributions restreignent leur lecture. Overlay se desactive alors
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
`overlay sensors` vous donnera.

**`cpu.temp` et `cpu.power` fonctionnent aussi sous Windows.** LibreHardwareMonitor
nomme sa sonde processeur d'apres le modele exact de la puce (« Core (Tctl/Tdie) »
chez AMD, « CPU Package » chez Intel...), ce qui rend son intitule imprevisible
d'une machine a l'autre. Overlay reconnait ces intitules — verifies contre le code
source de LibreHardwareMonitor, pas devines — et republie la bonne sonde (celle du
processeur entier, jamais un coeur ni un CCD isole) sous les cles stables
`cpu.temp`/`cpu.power`, en plus de sa cle `lhm.*` d'origine que `overlay sensors`
continue d'afficher pour le detail complet.

**L'index d'un GPU NVIDIA ne correspond pas forcement au numero que lui donne
Windows.** NVML/`nvidia-smi` ne numerotent que les cartes NVIDIA : avec un seul
GPU dedie, il porte toujours l'index 0 pour ces outils, meme si le Gestionnaire
des taches Windows — qui compte lui tous les adaptateurs, GPU integre compris —
l'appelle « GPU 1 ». Pour lever toute ambiguite, l'etiquette affichee reprend le
modele de la carte plutot qu'un numero (« RTX 4070 » au lieu de « GPU 0 ») des
qu'une seule carte NVIDIA est presente ; avec plusieurs cartes identiques, ou
l'index redevient la seule facon de les distinguer, il est ajoute en suffixe
(« RTX 4090 #0 », « RTX 4090 #1 »). Les cles (`gpu.0.temp`, `gpu.1.temp`...) ne
changent pas : seul l'affichage en est different.

**La RAM et la VRAM s'affichent « utilise / total »**, en Gio plutot qu'un chiffre
brut en MiB sans repere (`gpu.0.vram.used` : « 2.4 / 16.0 Go », `memory.used` :
« 11.5 / 32.0 Go »), sur l'overlay comme sur l'application mobile. Le total vient
de la meme information que la pleine echelle de la jauge, deja fournie par les
backends : aucune mesure supplementaire n'est necessaire pour ca.

Quand plusieurs sources publient la meme cle, la plus precise l'emporte, et les
temperatures psutil sont automatiquement desactivees des qu'une source dediee est
disponible. Sous Linux, le sous-repertoire hwmon d'une carte AMD est lu par le
backend `amdgpu` puis ignore par le backend hwmon generique : sa consommation
n'apparait donc qu'une fois. Sur une machine hybride, les cartes AMD sont
numerotees a la suite des cartes NVIDIA, pour que les deux ne se disputent pas
`gpu.0`.

`overlay sensors` liste les cles reellement disponibles sur votre machine ; ce
sont elles qu'on met dans `[overlay] metrics`.


## Configuration

```bash
overlay config --init   # cree le fichier a partir du modele commente
overlay config --path   # affiche son emplacement
```

Le fichier vit dans le repertoire de configuration de l'utilisateur
(`~/.config/overlay/config.toml` sous Linux,
`%LOCALAPPDATA%\overlay\config.toml` sous Windows) ; `--config` accepte un autre
chemin. Toute option omise garde sa valeur par defaut, et une valeur invalide est
refusee au demarrage avec un message explicite plutot qu'en cours de route.

Le modele complet et commente se trouve dans
[`src/overlay/data/config.example.toml`](src/overlay/data/config.example.toml).
Les reglages les plus utiles :

```toml
[general]
poll_interval = 1.0        # cadence d'echantillonnage, en secondes

[overlay]
position = "top-left"      # ou top-right, bottom-left, bottom-right
opacity = 0.85
columns = 2                # repartir les lignes sur plusieurs colonnes
hotkey = "<ctrl>+<alt>+o"  # raccourci global d'affichage
hotkey_quit = "<ctrl>+<alt>+q"  # raccourci global pour quitter
tray_icon = true           # icone de zone de notification (appairage, quitter)
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
dans `Authorization: Bearer …`, dans l'en-tete `X-Overlay-Token` ou dans le
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
  sous Wayland pur, le raccourci peut rester inactif — Overlay le signale au
  demarrage plutot que d'echouer en silence.
- **PresentMon** demande les droits administrateur.
- **Ventilateurs a zero.** Une vitesse de 0 RPM est generalement un connecteur
  libre sur la carte mere, pas une panne.


## Developpement

```bash
pip install -e ".[dev,overlay]"
python -m pytest -q                  # 293 tests
python -m ruff check src tests tools
python tools/make_icons.py           # regenere les icones de la PWA

# Rejoue la suite avec une horloge monotone a faible resolution
python -m pytest -q -p tests.coarse_clock
```

Ce dernier point merite un mot. Sous Windows, avant Python 3.13,
`time.monotonic()` avance par pas d'environ 15 ms : deux appels rapproches
renvoient la meme valeur. Tout ce qui deduit une grandeur d'un ecart de temps —
les debits disque et reseau, la peremption du flux d'images — s'y comporte
autrement, et la CI ne le revelait qu'apres coup. Le greffon `tests.coarse_clock`
quantifie l'horloge de la meme facon et rend le probleme reproductible sur
n'importe quelle machine ; un job dedie le rejoue a chaque push.

Les tests de l'overlay demandent PySide6 et sont sautes automatiquement s'il est
absent ; en environnement sans affichage, `QT_QPA_PLATFORM=offscreen` suffit. La
logique de connexion de l'application mobile est testee en chargeant `app.js` dans
un contexte Node muni d'un DOM et d'un WebSocket factices (`tests/webapp/`) ; ces
tests sont sautes si Node n'est pas installe.

Organisation du code :

| Chemin | Role |
| --- | --- |
| `src/overlay/models.py` | `Reading` et `Snapshot`, le vocabulaire partage par tout le reste |
| `src/overlay/sensors/` | Un module par source materielle, plus la detection automatique |
| `src/overlay/fps/` | Calcul des metriques de fluidite et sources de trames |
| `src/overlay/hub.py` | Boucle d'echantillonnage et diffusion aux abonnes |
| `src/overlay/server/` | API HTTP/WebSocket, jeton, appairage |
| `src/overlay/webapp/` | L'application mobile (HTML/CSS/JS, sans dependance) |
| `src/overlay/overlay/` | Fenetre Qt et raccourci global |

Ajouter une source de capteurs revient a implementer `SensorBackend` (`available()`
et `read()`) puis a la declarer dans `detect_backends()` : l'overlay, l'API et
l'application mobile la reprennent sans modification.


## Licence

MIT — voir [LICENSE](LICENSE).
