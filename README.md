# Overlay

Monitoring materiel du PC affiche par-dessus le jeu, avec une application Android
compagnon.

Affiche en temps reel les images par seconde, les temperatures, les charges CPU et
GPU, la consommation electrique du processeur et de la carte graphique, la memoire
et les vitesses de ventilateur — a l'ecran par-dessus le jeu, et sur le telephone.
Chaque mesure peut etre montree ou masquee a la demande, et l'overlay entier
s'ouvre et se ferme par un raccourci clavier. Une icone dans la zone de
notification donne acces a l'appairage du telephone, aux reglages et permet de
quitter sans repasser par un terminal ; Ctrl-C fonctionne aussi.

```
┌──────────────────────────────────────────────────────────┐
│  OverlayPerf.exe (C#/.NET 8, Windows, droits administrateur) │
│                                                           │
│  capteurs ──► hub ──┬──► overlay (a l'ecran, par-dessus le jeu)
│  (LibreHardware-    │                                    │
│   Monitor integre,  └──► serveur HTTP/WS ────────────────┼──► telephone Android (app native)
│   compteurs Windows)                                     │     ou navigateur (PWA embarquee)
│  + PresentMon (FPS)      icone de notification, journaux │
└──────────────────────────────────────────────────────────┘
```

Les deux sorties lisent le meme flux de mesures : ce que montre l'overlay et ce que
montre le telephone sont toujours coherents.


## Les deux applications

- **[`dotnet/`](dotnet/README.md)** — `OverlayPerf.exe`, un executable Windows
  unique, sans rien a installer a part lui. Demande les droits administrateur au
  lancement (invite UAC), integre LibreHardwareMonitor (plus besoin de le lancer a
  part), tient un journal quotidien dans `%LOCALAPPDATA%\overlay\logs`, et sert a la
  fois l'overlay a l'ecran, le serveur pour le telephone et une fenetre Parametres.
- **[`mobile/`](mobile/README.md)** — application Android native (Flutter), avec un
  blocage de veille fiable (natif, contrairement a l'API Wake Lock du navigateur) :
  garde l'ecran allume en permanence pour suivre la telemetrie pendant une session
  de jeu. Scan du QR code d'appairage integre : aucune saisie manuelle du jeton.

Chaque dossier a son propre README avec les instructions completes (installation,
configuration, compilation). Celui-ci couvre ce qui est commun aux deux : le
protocole, la securite, et ce qui est mesure.


## Demarrage rapide

1. Telechargez [PresentMon](https://github.com/GameTechDev/PresentMon/releases)
   (`PresentMon-<version>-x64.exe`, l'outil console — voir l'avertissement plus bas
   sur le nom ambigu) et placez-le a cote de `OverlayPerf.exe`, ou indiquez son
   chemin dans `[fps] presentmon_path`.
2. Double-cliquez sur `OverlayPerf.exe`, acceptez l'invite UAC. Une notification
   confirme le demarrage et l'overlay apparait a l'ecran.
3. Depuis l'icone de la zone de notification : **Appairer un telephone…** affiche
   un QR code.
4. Installez l'application Android (voir [mobile/README.md](mobile/README.md)) et
   scannez ce QR code depuis ses Reglages : adresse et jeton se remplissent tout
   seuls.

Aucun materiel sous la main ? `mock = true` dans `[general]` remplace tous les
capteurs par des valeurs simulees, de quoi regler l'affichage tranquillement.


## Acceder depuis un navigateur (sans l'application Android)

`OverlayPerf.exe` sert lui-meme une application web installable (PWA), a la meme
adresse que l'API : utile sur un appareil ou l'APK Android n'est pas installe
(iPhone, tablette, PC portable). Le blocage de veille y est moins fiable qu'avec
l'application native (raison d'etre de cette derniere), mais tout le reste — les
mesures, l'appairage, le jeton — est strictement identique.

Ouvrez l'adresse affichee par « Appairer un telephone… » dans un navigateur, puis
« Ajouter a l'ecran d'accueil » depuis son menu pour une icone dediee.


## Quitter proprement

- **Ctrl-C** dans le terminal fonctionne si l'exe a ete lance depuis un terminal.
- **Un raccourci global** (`<ctrl>+<alt>+q` par defaut, configurable via
  `[overlay] hotkey_quit`) quitte sans avoir a revenir a un terminal.
- **L'icone de zone de notification** propose « Quitter » dans son menu.

Dans les trois cas, OverlayPerf arrete proprement le serveur, la collecte et
PresentMon avant de sortir.

L'application (Android comme navigateur) retient **deux adresses** : celle du
reseau local et, si vous en configurez une, celle joignable depuis l'exterieur.
Elle essaie la locale en premier — sur place, elle evite le detour par Internet —
puis bascule sur la distante en une seconde si elle ne repond pas. L'etat affiche
laquelle est en service (« En direct · local » ou « En direct · distant »). La
connexion se retablit toute seule apres une coupure Wi-Fi ou une sortie de veille,
avec un recul exponentiel pour ne pas marteler l'agent.


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
idealement, un port non standard. OverlayPerf vous avertit au demarrage si
`public_url` est en HTTP.

### Ce que fait OverlayPerf de son cote

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
- **Rotation du jeton.** `OverlayPerf.exe --pair --rotate` en genere un nouveau et
  invalide les telephones deja appaires. A faire au moindre doute.


## Ce qui est mesure, et comment

### Images par seconde

OverlayPerf ne s'injecte dans aucun jeu. [PresentMon](https://github.com/GameTechDev/PresentMon)
(Intel, open source) lit les traces ETW de presentation DXGI/D3D/Vulkan : il mesure
donc tous les jeux, y compris en plein ecran exclusif. **Demande les droits
administrateur** (deja acquis au demarrage grace au manifeste de l'exe).

**FPS suit l'application au premier plan**, comme les autres compteurs d'images
(RTSS, Special K...) : OverlayPerf verifie toutes les deux secondes quelle fenetre
a le focus et cible PresentMon exclusivement sur ce processus. Alt-tabbez vers un
jeu et sa mesure demarre en quelques secondes, sans redemarrer OverlayPerf ; passez
au bureau ou a une autre application et la mesure precedente reste affichee jusqu'a
ce qu'un nouveau jeu prenne le focus (afficher le FPS de l'Explorateur ou d'un
navigateur n'aurait pas de sens). Tant qu'aucune application n'a encore ete
identifiee (juste apres le demarrage), un filet de securite a base de liste noire
evite de mesurer les processus systeme connus (Explorateur, Gestionnaire des
taches...) le temps que le vrai jeu prenne le focus.

**Attention au nom : deux outils differents s'appellent tous les deux
`PresentMon.exe`.** Le depot [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon)
publie a la fois l'**outil console** attendu ici (le binaire de release porte un
nom versionne, par exemple `PresentMon-2.5.1-x64.exe`) et une **application
graphique** distincte, « PresentMon Capture » (fenetre avec reglages, hotkeys,
auto-target), qui se lance elle sous le nom `PresentMon.exe`. Pour lever toute
ambiguite, placez le binaire versionne a cote de `OverlayPerf.exe`, ou indiquez
son chemin exact :

```toml
[fps]
presentmon_path = "C:/Chemin/Vers/PresentMon-2.5.1-x64.exe"
```

D'autres modes existent (`[fps] mode`) : `push` (votre propre programme publie ses
trames sur `POST /api/fps/frame`, utile quand PresentMon ne convient pas),
`presentmon` (PresentMon obligatoire, erreur explicite s'il manque), `auto` (le
defaut), `off` (desactive completement la mesure).

Outre le FPS moyen, OverlayPerf publie le temps de trame et les centiles bas
(**1 % low** et **0,1 % low**), c'est-a-dire l'inverse des 99e et 99,9e centiles de
duree de trame. Ce sont eux qui decrivent les saccades que la moyenne masque. Les
interruptions de flux (alt-tab, ecran de chargement) sont ecartees pour ne pas
fausser durablement ces centiles.

Le journal (`%LOCALAPPDATA%\overlay\logs`) explique toujours pourquoi FPS reste a
« — » : PresentMon introuvable, droits administrateur manquants, ou processus
demarre mais aucune trame recue — chaque cas produit un message different plutot
qu'un silence.

### Temperatures, ventilateurs, charges, consommation

Windows n'expose aucune API publique pour les temperatures et les ventilateurs :
il faut un pilote en mode noyau. [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
le fournit — **integre directement dans `OverlayPerf.exe`**, rien a installer ni
lancer a part. Les droits administrateur (deja acquis au demarrage) suffisent a
charger le pilote.

| Source | Couverture |
| --- | --- |
| LibreHardwareMonitor (integre) | temperatures, ventilateurs, consommations, frequences, carte mere, SSD/NVMe |
| NVML / `nvidia-smi` | GPU NVIDIA, en secours si LibreHardwareMonitor n'en voit aucun |
| Compteurs de performance Windows | charge CPU (globale ou par coeur), memoire, debits disque et reseau |

**La carte dediee est toujours `gpu.0`, les puces graphiques integrees viennent
ensuite.** Sur une machine avec un processeur a partie graphique integree en plus
d'une carte dediee, l'ordre de detection ne correspond pas forcement a celui du
Gestionnaire des taches : OverlayPerf trie explicitement pour que ce soit toujours
la carte dediee qui apparaisse en premier.

**Ventilateurs GPU en tours/minute.** En plus du pourcentage (`gpu.0.fan`), la
vitesse reelle est publiee sous `gpu.0.fan.rpm` (et `gpu.0.fan.2.rpm` pour une
carte a plusieurs ventilateurs) quand la sonde existe.

**La RAM et la VRAM s'affichent « utilise / total »**, en Gio plutot qu'un chiffre
brut en MiB sans repere (`gpu.0.vram.used` : « 2.4 / 16.0 Go », `memory.used` :
« 11.5 / 32.0 Go »), sur l'overlay comme sur l'application. Le total vient de la
meme information que la pleine echelle de la jauge, deja fournie par les backends.

`OverlayPerf.exe --sensors` liste les cles reellement disponibles sur votre
machine ; ce sont elles qu'on met dans `[overlay] metrics` (ou qu'on coche/decoche
dans la fenetre Parametres…).


## Configuration

```bash
OverlayPerf.exe --config-init   # cree le fichier a partir du modele commente
```

Le fichier vit dans `%LOCALAPPDATA%\overlay\config.toml` ; `--config` accepte un
autre chemin. Toute option omise garde sa valeur par defaut, et une valeur
invalide est refusee au demarrage avec un message explicite plutot qu'en cours de
route. La plupart des reglages d'affichage (position, opacite du fond et des
informations, police, colonnes, raccourcis, mesures affichees) se regnent
directement depuis la fenetre **Parametres…** de l'icone de zone de notification,
qui ecrit dans ce meme fichier sans toucher a ses commentaires.

```toml
[general]
poll_interval = 1.0        # cadence d'echantillonnage, en secondes

[overlay]
position = "top-left"      # ou top-right, bottom-left, bottom-right
opacity = 0.75              # opacite du fond (cartouche)
text_opacity = 1.0           # opacite des informations (texte), independante
columns = 2                # repartir les lignes sur plusieurs colonnes
hotkey = "<ctrl>+<alt>+o"  # raccourci global d'affichage
hotkey_quit = "<ctrl>+<alt>+q"  # raccourci global pour quitter
tray_icon = true           # icone de zone de notification
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
| `GET /api/overlay` | Selection et mise en forme de l'overlay a l'ecran (position, opacites, cles affichees, dans l'ordre) |
| `GET /api/fps` | Detail du compteur d'images (moyenne, centiles bas, application) |
| `POST /api/fps/frame` | Publier une trame : `{"frame_time_ms": 8.33}` |
| `WS /ws` | Flux temps reel, un message JSON par cycle de collecte |

```bash
curl -H "Authorization: Bearer $JETON" \
     "http://127.0.0.1:8777/api/metrics?keys=cpu.load,gpu.0.temp"
```

`GET /api/overlay` est ce qui permet a l'application Android d'afficher exactement
la meme chose que l'overlay a l'ecran (page « Overlay »), sans dupliquer la
configuration.


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
  tunnel chiffre** — voir la section dediee plus haut. (L'application Android
  declare volontairement `usesCleartextTraffic` pour cette meme raison : voir
  [mobile/README.md](mobile/README.md).)


## Limites connues

- **Plein ecran exclusif.** Un jeu DirectX en plein ecran exclusif dessine
  directement sur le balayage ecran et masque toute fenetre, overlay compris. En
  plein ecran fenetre ou sans bordure — le mode par defaut de la plupart des jeux
  recents — l'overlay s'affiche normalement. Le telephone reste de toute facon une
  sortie utilisable, et c'est meme sa principale raison d'etre.
- **PresentMon** demande les droits administrateur, deja acquis au demarrage.
- **Ventilateurs a zero.** Une vitesse de 0 RPM est generalement un connecteur
  libre sur la carte mere, ou un mode zero-fan au repos, pas une panne.
- **Windows uniquement.** Cette version ne couvre pas Linux/macOS.


## Developpement

Voir [dotnet/README.md](dotnet/README.md) (compilation de `OverlayPerf.exe`,
structure du code, tests) et [mobile/README.md](mobile/README.md) (compilation de
l'APK, structure du code Flutter, tests) pour les instructions completes de chaque
application.


## Licence

MIT — voir [LICENSE](LICENSE).
