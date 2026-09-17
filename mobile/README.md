# Overlay — application Android native

Application Flutter native (Android) qui remplace l'application web (PWA) pour l'usage
principal : garder l'écran allumé en permanence pour suivre la télémétrie du PC, chose que
l'API Wake Lock du navigateur ne garantit pas de façon fiable (coupée en arrière-plan, sur
mise en veille de l'onglet, selon le navigateur).

## Pourquoi une app native plutôt que la PWA

| | PWA (navigateur) | Cette application |
| --- | --- | --- |
| Blocage de veille | `navigator.wakeLock`, se coupe facilement (onglet masqué, batterie faible, non supporté partout) | `WindowManager.FLAG_KEEP_SCREEN_ON` natif via `wakelock_plus`, fiable tant que l'app est au premier plan |
| Installation | "Ajouter à l'écran d'accueil" | APK installable, icône, nom "Overlay" |
| Connexion, données, jeton | identiques | identiques (même WebSocket, même JSON) |

Le reste (mesures, groupes, jauges, adresses locale/distante, jeton) est un miroir
fonctionnel de la PWA — même protocole, même comportement de reconnexion.

## Deux pages

- **Overlay** : exactement ce que l'overlay affiche à l'écran du PC, dans le même ordre.
  Relit `GET /api/overlay` toutes les 20 s : un changement dans la fenêtre Paramètres…
  du PC se répercute sur le téléphone sans rien faire. Si cette route ne répond pas
  (agent trop ancien), un message l'indique et affiche tout par défaut.
- **Tout le reste** : les autres mesures, groupées par famille (CPU, GPU, mémoire,
  ventilateurs...), avec un bouton pour en masquer sur ce téléphone (persistant, propre
  à cet appareil).

## Réglages

Icône engrenage en haut à droite : un bouton **« Scanner le QR code du PC »** (aussi
proposé directement sur l'écran d'accueil tant qu'aucune adresse n'est configurée) ouvre
la caméra, vise le QR code affiché par « Appairer un téléphone » sur le PC, et remplit
adresse + jeton d'un coup — plus besoin de recopier le jeton à la main. En dessous :
adresse locale, adresse distante (tunnel), jeton d'accès en saisie manuelle si besoin, et
**bloquer la mise en veille de l'écran** (activé par défaut — c'est la raison d'être de
l'application).

## Compiler

Prérequis : [Flutter](https://flutter.dev) (testé avec 3.44) et le SDK Android.

```bash
cd mobile
flutter pub get
flutter analyze
flutter test
flutter build apk --release              # APK universel (~48 Mo, toutes architectures)
flutter build apk --release --split-per-abi  # un APK par architecture (~15-18 Mo chacun)
```

L'APK se trouve dans `build/app/outputs/flutter-apk/`. Pas de compte Google Play requis :
installation par transfert direct du fichier `.apk` (activer « Sources inconnues » côté
Android au premier lancement).

**Note Windows** : si le dépôt est sur un lecteur différent du cache Gradle/Pub (`D:`
contre `C:`, par exemple), la compilation incrémentale Kotlin plante avec *"this and base
files have different roots"*. `android/gradle.properties` désactive `kotlin.incremental`
pour contourner ce bug connu — la compilation est un peu plus lente, mais fonctionne.

## Trafic en clair (HTTP) sur le réseau local

`android:usesCleartextTraffic="true"` est déclaré volontairement : `OverlayPerf.exe`
écoute en HTTP simple sur le réseau local par conception (voir la section Sécurité du
README principal) — Android 9+ bloque sinon toute connexion HTTP par défaut. Pour un
accès à distance, l'agent recommande un tunnel HTTPS (Tailscale, Cloudflare Tunnel...),
auquel cas la connexion redevient chiffrée de bout en bout.

## Icône

Même dessin que l'overlay à l'écran et l'exécutable Windows (barres croissantes) :
icône adaptative Android (premier plan `android/app/src/main/res/drawable/ic_launcher_foreground.png`
+ fond uni `#0B0F17`) générée par `flutter_launcher_icons` depuis `assets/icon/icon.png`.
Pour régénérer après une modification du dessin : `dart run flutter_launcher_icons`.

## Structure

```
lib/
  main.dart                    point d'entree, theme, câblage
  models.dart                  Reading, Snapshot, OverlaySelection (miroir du JSON de l'agent)
  settings.dart                reglages persistants (SharedPreferences)
  connection_service.dart      WebSocket, reconnexion, bascule local/distant, /api/overlay
  pairing.dart                  decodage de l'URL d'appairage scannee (adresse + jeton)
  formatting.dart               mise en forme des valeurs et couleurs (miroir de app.js / OverlayForm.cs)
  history.dart                  historique borne pour les mini-courbes
  theme.dart                     palette sombre (miroir de style.css)
  screens/home_screen.dart      les deux pages, la barre de statut, le choix des mesures
  screens/settings_screen.dart  adresses, jeton, verrou d'ecran, bouton de scan
  screens/qr_scan_screen.dart   camera (mobile_scanner), lecture du QR code d'appairage
  widgets/reading_tile.dart     tuile (jauge ou mini-courbe)
```
