# OverlayPerf — agent Windows en C#/.NET

**Exécutable Windows unique** (`OverlayPerf.exe`), sans rien d'autre à installer.
Overlay de monitoring par-dessus le jeu (FPS, températures, charges, consommations,
ventilateurs), icône dans la zone de notification, et serveur pour l'application
mobile.

```
┌──────────────────────────────────────────────────────────┐
│  OverlayPerf.exe (C#/.NET 8, droits administrateur)      │
│                                                          │
│  capteurs ──► hub ──┬──► overlay WinForms (click-through) │
│  (LibreHardware-    │                                    │
│   Monitor intégré,  └──► serveur Kestrel HTTP/WS ────────┼──► téléphone (PWA)
│   compteurs Windows)                                     │
│  + PresentMon (FPS)      icône de notification, journaux │
└──────────────────────────────────────────────────────────┘
```

## Historique : pourquoi cette réécriture

Ce projet a démarré comme un agent Python (multiplateforme). Cette version C#/.NET l'a
entièrement remplacé sous Windows, avant que l'agent Python ne soit retiré du dépôt.

| | Ancien agent Python | OverlayPerf.exe |
| --- | --- | --- |
| Installation | Python + `pip install` + PySide6 | un seul `.exe` (autonome, ~75 Mo) |
| Droits administrateur | à penser soi-même (terminal en admin) | **invite UAC au lancement**, imposée par le manifeste |
| Températures / ventilateurs | LibreHardwareMonitor **à lancer à part**, serveur web à activer | bibliothèque LibreHardwareMonitor **intégrée** : rien à installer |
| GPU NVIDIA | NVML ou `nvidia-smi` | LibreHardwareMonitor (NVML) ; `nvidia-smi` en secours |
| Diagnostic | messages console | **journal quotidien** dans `%LOCALAPPDATA%\overlay\logs`, notifications dans la zone de notification |
| PresentMon | erreurs de lancement invisibles (stderr jeté) | stderr capturé et journalisé, relance automatique, message clair si droits insuffisants |
| Linux (MangoHud, hwmon, RAPL) | oui | non : Windows uniquement |

La configuration (`%LOCALAPPDATA%\overlay\config.toml`), le jeton, l'API HTTP/WebSocket et
l'application mobile sont **identiques** : un téléphone déjà appairé continue de fonctionner,
un `config.toml` existant est relu tel quel (les options propres à Linux sont ignorées, avec un
avertissement dans le journal).

## Utilisation

1. Téléchargez l'outil console [PresentMon](https://github.com/GameTechDev/PresentMon/releases)
   (`PresentMon-<version>-x64.exe`) et placez-le **à côté de `OverlayPerf.exe`** — ou indiquez
   son chemin dans `[fps] presentmon_path`. Sans lui, tout fonctionne sauf le FPS.
2. Double-cliquez sur `OverlayPerf.exe` et acceptez l'invite UAC.
3. Une notification confirme le démarrage ; l'icône dans la zone de notification donne accès à :
   - **Afficher / masquer l'overlay** (aussi par `Ctrl+Alt+O`, clic gauche sur l'icône) ;
   - **Paramètres…** : écran d'affichage (si plusieurs moniteurs), coin de l'écran, **opacité
     du fond et opacité des informations** (deux curseurs, aperçu en direct), marge, police,
     colonnes, click-through, raccourcis, chemin de PresentMon, mesure du multiplicateur de
     génération d'images (beta), et la liste des mesures de l'overlay à cocher/décocher et
     ordonner (« Appliquer » agit à chaud, « OK » enregistre dans `config.toml` en gardant ses
     commentaires ; le chemin de PresentMon et le multiplicateur de génération d'images
     demandent de relancer OverlayPerf) ;
   - **Appairer un téléphone…** : QR code et adresse à scanner ;
   - **Ouvrir le journal du jour**, **le dossier des journaux** ;
   - **État…** : capteurs actifs, PresentMon, serveur, avertissements ;
   - **Quitter** (aussi par `Ctrl+Alt+Q`).

L'application mobile a deux pages : **Overlay** montre exactement ce que l'overlay affiche à
l'écran, dans le même ordre (relu toutes les 20 s depuis `GET /api/overlay`, donc un changement
dans Paramètres… se propage au téléphone tout seul) ; **Tout le reste** regroupe les autres
mesures par famille, avec le bouton « Mesures » pour en masquer sur ce téléphone.

Depuis un terminal, quelques commandes de diagnostic (l'invite UAC apparaît aussi) :

```bash
OverlayPerf.exe --sensors        # capteurs détectés et instantané des mesures
OverlayPerf.exe --pair           # adresse et QR code pour le téléphone
OverlayPerf.exe --pair --rotate  # nouveau jeton (invalide les téléphones déjà appariés)
OverlayPerf.exe --config-init    # écrire un config.toml d'exemple
OverlayPerf.exe --help
```

Options : `--no-overlay`, `--no-server`, `--mock` (valeurs simulées), `--config <chemin>`.

## Journaux

Un fichier par jour, `overlay-AAAAMMJJ.log`, dans `%LOCALAPPDATA%\overlay\logs` (7 jours
conservés, niveau réglable par `[general] log_level = "debug"`). On y trouve, dans l'ordre :
la configuration chargée et ses options inconnues, les droits, chaque composant matériel vu par
LibreHardwareMonitor et l'ordre des GPU, la ligne de commande PresentMon et **tout ce qu'il écrit
sur stderr**, la première trame reçue, les connexions du téléphone, et chaque erreur avec sa
trace. Le menu « Ouvrir le journal du jour » y mène directement.

## Configuration

Même fichier et mêmes clés que l’ancien agent Python. Nouveautés :

```toml
[general]
log_level = "info"          # debug, info, warning, error
log_retention_days = 7

[sensors]
disabled = []               # "lhm", "nvidia", "system"

[overlay]
opacity = 0.75              # fond (cartouche), 0.0 = aucun fond
text_opacity = 1.0          # informations (texte et liseré)
```

L'overlay est dessiné en transparence par pixel (`UpdateLayeredWindow`) : les deux opacités
sont indépendantes, et le texte garde son liseré net quelle que soit la transparence du fond.

Les options `[sensors] lhm_url`, `[fps] mangohud_log_dir` et le mode `mangohud` sont acceptés
mais ignorés (journalisés).

**Multiplicateur de génération d'images** (`fps.framegen`, DLSS/FSR/XeSS Frame Generation) :
option beta de PresentMon (`--track_frame_type`), à activer dans Paramètres… ou
`[fps] track_frame_generation = true`. Nécessite une version récente de PresentMon **et** que
le jeu/pilote l'instrumente ; si l'exécutable configuré ne reconnaît pas l'option, elle est
désactivée automatiquement pour la session (journalisé) plutôt que de casser tout le FPS.
Absent de tout ça, la mesure reste à « — ».

Clés de mesures publiées : `fps.*` (dont `fps.application`, le processus actuellement mesuré,
et `fps.framegen`, le multiplicateur de génération d'images — voir plus bas), `cpu.load`, `cpu.temp`, `cpu.power`, `cpu.clock`,
`gpu.N.load|temp|hotspot|fan|fan.rpm|fan.2.rpm|vram.used|vram.load|power|clock.core|clock.mem`
(`fan` en %, `fan.rpm` et `fan.2.rpm` en tours/minute par ventilateur ; la carte dédiée est
toujours `gpu.0`, les puces intégrées viennent après), `memory.load|used|total|commit`,
`fan.<composant>.<nom>` (carte mère, AIO), `temp.<disque>`, `network.in|out`,
`storage.read|write`, et le catalogue brut `lhm.<matériel>.<type>.<sonde>`.
`OverlayPerf.exe --sensors` liste ce qui existe réellement sur votre machine.

## Compiler

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) sous Windows.

```bash
cd dotnet
dotnet test                                                   # 76 tests
dotnet publish OverlayPerf/OverlayPerf.csproj -c Release -o publish
```

`publish\OverlayPerf.exe` est autonome (runtime inclus). Le manifeste
`OverlayPerf/app.manifest` porte `requireAdministrator` : c'est lui qui déclenche l'invite UAC.
Pour tester sans élévation pendant le développement, lancer la DLL directement contourne le
manifeste : `dotnet bin/Release/net8.0-windows/win-x64/OverlayPerf.dll --no-elevate`.

### Installeur Windows

`installer/OverlayPerf.iss` ([Inno Setup](https://jrsoftware.org/isinfo.php)) enveloppe
`publish/OverlayPerf.exe` dans un installeur classique (raccourcis menu Démarrer et bureau,
désinstalleur). Pas de lancement automatique au démarrage de Windows : `OverlayPerf.exe`
exige les droits administrateur (`app.manifest`), et ni un raccourci dans le dossier
Démarrage ni la clé de registre `Run` ne peuvent l'éléver — Windows bloque silencieusement
ce genre de lancement, sans invite ni erreur. Seule une tâche planifiée y arrive, ce qui a
été volontairement écarté (voir l'historique du dépôt) ; l'exécutable se lance donc à la main.

```bash
dotnet publish OverlayPerf/OverlayPerf.csproj -c Release -o publish   # d'abord l'exe
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\OverlayPerf.iss
```

Résultat : `installer/output/OverlayPerf-Setup-<version>.exe`. Compilé automatiquement en CI
(Inno Setup est préinstallé sur les runners `windows-latest`), artefact `OverlayPerf-Setup`.
La configuration, le jeton et les journaux (`%LOCALAPPDATA%\overlay`) sont conservés à la
désinstallation.

`OverlayPerf/app.ico` (même dessin que `webapp/icon-512.png` et l'icône Android) est à la
fois l'icône de l'exécutable (`ApplicationIcon`) et celle de la zone de notification : la
zone de notification reprend directement l'icône de l'exe (`Icon.ExtractAssociatedIcon`)
plutôt que d'embarquer une seconde image à maintenir séparément.

## Structure

```
OverlayPerf/
  Program.cs            point d'entrée, options, instance unique, élévation, commandes console
  App/Runtime.cs        assemblage capteurs + FPS + hub + serveur, texte d'état
  Config/               AppConfig (TOML), chemins, modèle, écriture ciblée de config.toml (TomlPatcher)
  Logging/LogSetup.cs   Serilog : fichier journalier + console si terminal
  Models/               Reading, Snapshot (même format JSON que l’ancien agent Python)
  Sensors/              LibreHardwareMonitor intégré, compteurs Windows, nvidia-smi, simulation
  Fps/                  FrameTimeTracker, parseur CSV PresentMon, sous-processus PresentMon, ciblage par application au premier plan
  Hub/MetricsHub.cs     boucle de collecte, historique, diffusion aux abonnés
  Server/               Kestrel : API, WebSocket, jeton, verrouillage, appairage/QR, PWA embarquée
  Ui/                   overlay WinForms, icône de notification, raccourcis globaux, Paramètres, appairage
  webapp/               application mobile (copie de src/overlay/webapp, embarquée dans l'exe)
OverlayPerf.Tests/      xUnit : tracker, parseur PresentMon, config, auth, appairage, raccourcis
```
