namespace OverlayPerf.Config;

/// <summary>Modele de <c>config.toml</c> ecrit a la demande (menu de l'icone, ou <c>--config-init</c>).</summary>
public static class ConfigTemplate
{
    public const string Text = """
        # Configuration d'OverlayPerf.
        #
        # Emplacement attendu : %LOCALAPPDATA%\overlay\config.toml (le meme que l'agent Python).
        # Toute option omise garde sa valeur par defaut. Les chemins Windows s'ecrivent avec des
        # barres obliques ("C:/Outils/PresentMon.exe") : en TOML, la barre inverse est un
        # caractere d'echappement.

        [general]
        # Periode d'echantillonnage des capteurs, en secondes (0.5 pour un compteur plus vif).
        poll_interval = 1.0

        # Remplace tous les capteurs par des valeurs simulees (demonstration, reglage de l'affichage).
        mock = false

        # Nombre de mesures conservees pour les courbes (300 = 5 min a 1 Hz).
        history_size = 300

        # Journaux dans %LOCALAPPDATA%\overlay\logs : debug, info, warning, error.
        log_level = "info"
        log_retention_days = 7


        [sensors]
        # Publier la charge de chaque coeur en plus de la charge globale.
        per_core = false

        # Publier les debits disque et reseau.
        include_io = true

        # Backends a ne pas charger : "lhm" (LibreHardwareMonitor integre), "nvidia" (nvidia-smi), "system".
        disabled = []


        [fps]
        # Source des images par seconde :
        #   auto        PresentMon s'il est trouve (chemin ci-dessous, a cote de l'executable, ou PATH)
        #   presentmon  PresentMon obligatoire : erreur explicite s'il manque
        #   push        aucune source locale : seul POST /api/fps/frame alimente le compteur
        #   off         desactive completement la mesure du FPS
        mode = "auto"

        # Chemin de l'outil console PresentMon (https://github.com/GameTechDev/PresentMon/releases,
        # fichier PresentMon-<version>-x64.exe). Vide : recherche automatique.
        presentmon_path = ""

        # Duree de la moyenne glissante du compteur, en secondes.
        window_seconds = 1.0


        [server]
        enabled = true

        # 0.0.0.0 rend le serveur joignable depuis le telephone ; 127.0.0.1 interdit tout acces reseau.
        host = "0.0.0.0"
        port = 8777

        # Laissez vide : un jeton aleatoire est genere puis conserve dans %LOCALAPPDATA%\overlay\token.
        token = ""

        # Mesures envoyees a l'application mobile. Vide = toutes.
        metrics = []

        # --- Acces depuis un autre reseau (tunnel Tailscale, Cloudflare...) : voir le README.
        public_url = ""
        behind_proxy = false
        trusted_proxies = "127.0.0.1"
        tls_cert = ""
        tls_key = ""
        max_auth_failures = 10
        auth_lockout_seconds = 300


        [overlay]
        enabled = true

        # top-left, top-right, bottom-left, bottom-right
        position = "top-left"
        margin = 24

        # Opacite du fond (0.0 = pas de cartouche) et des informations (texte), de 0 a 1.
        opacity = 0.75
        text_opacity = 1.0

        font_size = 14
        columns = 1

        # Laisser passer les clics vers le jeu situe dessous.
        click_through = true

        # Raccourcis globaux (syntaxe pynput conservee) : "<ctrl>+<alt>+o", "<shift>+<f10>"...
        hotkey = "<ctrl>+<alt>+o"
        hotkey_quit = "<ctrl>+<alt>+q"

        tray_icon = true
        visible_at_start = true

        # Mesures affichees, dans l'ordre. Un « * » final agit comme un prefixe.
        # « OverlayPerf.exe --sensors » liste les cles reellement disponibles sur votre machine.
        metrics = [
            "fps.current",
            "fps.low1",
            "fps.frametime",
            "cpu.load",
            "cpu.temp",
            "cpu.power",
            "gpu.0.load",
            "gpu.0.temp",
            "gpu.0.power",
            "gpu.0.vram.used",
            "gpu.0.fan.rpm",
            "memory.used",
            "fan.*",
        ]
        """;
}
