using Tomlyn;
using Tomlyn.Model;

namespace OverlayPerf.Config;

public sealed class ConfigException(string message) : Exception(message);

public sealed class GeneralConfig
{
    /// <summary>Periode d'echantillonnage des capteurs, en secondes.</summary>
    public double PollInterval { get; set; } = 1.0;
    /// <summary>Force le backend de demonstration (aucun materiel requis).</summary>
    public bool Mock { get; set; }
    /// <summary>Nombre de snapshots conserves pour les graphiques d'historique.</summary>
    public int HistorySize { get; set; } = 300;
    /// <summary>Niveau des journaux : debug, info, warning, error.</summary>
    public string LogLevel { get; set; } = "info";
    /// <summary>Nombre de fichiers journaliers conserves.</summary>
    public int LogRetentionDays { get; set; } = 7;
}

public sealed class SensorsConfig
{
    public bool PerCore { get; set; }
    public bool IncludeIo { get; set; } = true;
    /// <summary>Noms de backends a ne pas charger : lhm, nvidia, system.</summary>
    public List<string> Disabled { get; set; } = [];
}

public sealed class FpsConfig
{
    /// <summary>auto, presentmon, push ou off.</summary>
    public string Mode { get; set; } = "auto";
    public string PresentMonPath { get; set; } = "";
    public double WindowSeconds { get; set; } = 1.0;
}

public sealed class ServerConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>0.0.0.0 rend le serveur joignable depuis le telephone sur le reseau local.</summary>
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8777;
    /// <summary>Jeton partage exige par l'API ; genere et persiste automatiquement si vide.</summary>
    public string Token { get; set; } = "";
    public List<string> Metrics { get; set; } = [];
    public string PublicUrl { get; set; } = "";
    public bool BehindProxy { get; set; }
    public string TrustedProxies { get; set; } = "127.0.0.1";
    public string TlsCert { get; set; } = "";
    public string TlsKey { get; set; } = "";
    public int MaxAuthFailures { get; set; } = 10;
    public int AuthLockoutSeconds { get; set; } = 300;
}

public sealed class OverlayConfig
{
    public static readonly string[] DefaultMetrics =
    [
        "fps.current", "fps.low1", "fps.frametime",
        "cpu.load", "cpu.temp", "cpu.power",
        "gpu.0.load", "gpu.0.temp", "gpu.0.power", "gpu.0.vram.used", "gpu.0.fan.rpm",
        "memory.used",
        "fan.*",
    ];

    public bool Enabled { get; set; } = true;
    public string Position { get; set; } = "top-left";
    public int Margin { get; set; } = 24;
    /// <summary>Opacite du fond (cartouche sombre derriere les mesures).</summary>
    public double Opacity { get; set; } = 0.75;
    /// <summary>Opacite des informations (texte et son lisere), independante du fond.</summary>
    public double TextOpacity { get; set; } = 1.0;
    public int FontSize { get; set; } = 14;
    public int Columns { get; set; } = 1;
    /// <summary>Laisse passer clics et mouvements de souris vers la fenetre situee dessous.</summary>
    public bool ClickThrough { get; set; } = true;
    public string Hotkey { get; set; } = "<ctrl>+<alt>+o";
    public string HotkeyQuit { get; set; } = "<ctrl>+<alt>+q";
    public bool TrayIcon { get; set; } = true;
    public bool VisibleAtStart { get; set; } = true;
    public List<string> Metrics { get; set; } = [.. DefaultMetrics];
}

public sealed class AppConfig
{
    public static readonly string[] ValidPositions = ["top-left", "top-right", "bottom-left", "bottom-right"];
    public static readonly string[] ValidFpsModes = ["auto", "presentmon", "push", "off"];

    public GeneralConfig General { get; } = new();
    public SensorsConfig Sensors { get; } = new();
    public FpsConfig Fps { get; } = new();
    public ServerConfig Server { get; } = new();
    public OverlayConfig Overlay { get; } = new();

    /// <summary>Options presentes dans le fichier mais inconnues de cette version
    /// (typiquement les reglages Linux de l'agent Python : ignorees, mais signalees).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Charge la configuration ; un fichier absent donne les valeurs par defaut.</summary>
    public static AppConfig Load(string? path = null)
    {
        var target = path ?? Paths.ConfigFile;
        if (!File.Exists(target))
        {
            return new AppConfig();
        }
        return Parse(File.ReadAllText(target), target);
    }

    public static AppConfig Parse(string text, string sourceName = "config.toml")
    {
        var config = new AppConfig();
        // Tomlyn 2.10 : le modele generique TomlTable joue le role de dictionnaire dynamique.
        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new ConfigException($"{sourceName} : {ex.Message}");
        }
        var known = new HashSet<string>();

        void Section(string name, Action<SectionReader> apply)
        {
            known.Add(name);
            if (!model.TryGetValue(name, out var raw))
            {
                return;
            }
            if (raw is not TomlTable table)
            {
                throw new ConfigException($"[{name}] doit etre une table TOML");
            }
            var reader = new SectionReader(name, table, config.Warnings);
            apply(reader);
            reader.Finish();
        }

        Section("general", s =>
        {
            var g = config.General;
            g.PollInterval = s.Double("poll_interval", g.PollInterval);
            g.Mock = s.Bool("mock", g.Mock);
            g.HistorySize = s.Int("history_size", g.HistorySize);
            g.LogLevel = s.String("log_level", g.LogLevel).Trim().ToLowerInvariant();
            g.LogRetentionDays = s.Int("log_retention_days", g.LogRetentionDays);
        });
        Section("sensors", s =>
        {
            var se = config.Sensors;
            se.PerCore = s.Bool("per_core", se.PerCore);
            se.IncludeIo = s.Bool("include_io", se.IncludeIo);
            se.Disabled = s.StringList("disabled", se.Disabled).Select(d => d.Trim().ToLowerInvariant()).ToList();
            // Reglage de l'agent Python (LibreHardwareMonitor interroge via HTTP) : ici
            // la bibliotheque est integree, aucune URL n'est necessaire.
            s.Ignore("lhm_url", "LibreHardwareMonitor est integre a cet executable");
        });
        Section("fps", s =>
        {
            var f = config.Fps;
            f.Mode = s.String("mode", f.Mode).Trim().ToLowerInvariant();
            f.PresentMonPath = s.String("presentmon_path", f.PresentMonPath);
            f.WindowSeconds = s.Double("window_seconds", f.WindowSeconds);
            s.Ignore("mangohud_log_dir", "MangoHud est specifique a Linux");
        });
        Section("server", s =>
        {
            var srv = config.Server;
            srv.Enabled = s.Bool("enabled", srv.Enabled);
            srv.Host = s.String("host", srv.Host);
            srv.Port = s.Int("port", srv.Port);
            srv.Token = s.String("token", srv.Token);
            srv.Metrics = s.StringList("metrics", srv.Metrics);
            srv.PublicUrl = s.String("public_url", srv.PublicUrl);
            srv.BehindProxy = s.Bool("behind_proxy", srv.BehindProxy);
            srv.TrustedProxies = s.String("trusted_proxies", srv.TrustedProxies);
            srv.TlsCert = s.String("tls_cert", srv.TlsCert);
            srv.TlsKey = s.String("tls_key", srv.TlsKey);
            srv.MaxAuthFailures = s.Int("max_auth_failures", srv.MaxAuthFailures);
            srv.AuthLockoutSeconds = s.Int("auth_lockout_seconds", srv.AuthLockoutSeconds);
        });
        Section("overlay", s =>
        {
            var ov = config.Overlay;
            ov.Enabled = s.Bool("enabled", ov.Enabled);
            ov.Position = s.String("position", ov.Position);
            ov.Margin = s.Int("margin", ov.Margin);
            ov.Opacity = s.Double("opacity", ov.Opacity);
            ov.TextOpacity = s.Double("text_opacity", ov.TextOpacity);
            ov.FontSize = s.Int("font_size", ov.FontSize);
            ov.Columns = s.Int("columns", ov.Columns);
            ov.ClickThrough = s.Bool("click_through", ov.ClickThrough);
            ov.Hotkey = s.String("hotkey", ov.Hotkey);
            ov.HotkeyQuit = s.String("hotkey_quit", ov.HotkeyQuit);
            ov.TrayIcon = s.Bool("tray_icon", ov.TrayIcon);
            ov.VisibleAtStart = s.Bool("visible_at_start", ov.VisibleAtStart);
            ov.Metrics = s.StringList("metrics", ov.Metrics);
        });

        foreach (var key in model.Keys)
        {
            if (!known.Contains(key))
            {
                config.Warnings.Add($"Section inconnue ignoree : [{key}]");
            }
        }

        // Le mode MangoHud de l'agent Python n'a pas d'equivalent Windows : on se
        // rabat sur l'API HTTP plutot que de refuser de demarrer.
        if (config.Fps.Mode == "mangohud")
        {
            config.Warnings.Add("[fps] mode = \"mangohud\" est specifique a Linux : mode \"push\" utilise a la place");
            config.Fps.Mode = "push";
        }

        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (General.PollInterval <= 0)
            throw new ConfigException("[general] poll_interval doit etre strictement positif");
        if (General.HistorySize < 1)
            throw new ConfigException("[general] history_size doit valoir au moins 1");
        if (!ValidFpsModes.Contains(Fps.Mode))
            throw new ConfigException($"[fps] mode doit etre parmi {string.Join(", ", ValidFpsModes)}");
        if (Fps.WindowSeconds <= 0)
            throw new ConfigException("[fps] window_seconds doit etre strictement positif");
        if (Server.Port is < 1 or > 65535)
            throw new ConfigException("[server] port doit etre compris entre 1 et 65535");
        if (Server.PublicUrl.Length > 0
            && !Server.PublicUrl.StartsWith("http://", StringComparison.Ordinal)
            && !Server.PublicUrl.StartsWith("https://", StringComparison.Ordinal))
            throw new ConfigException("[server] public_url doit commencer par http:// ou https://");
        if (string.IsNullOrEmpty(Server.TlsCert) != string.IsNullOrEmpty(Server.TlsKey))
            throw new ConfigException("[server] tls_cert et tls_key vont de pair");
        foreach (var (champ, valeur) in new[] { ("tls_cert", Server.TlsCert), ("tls_key", Server.TlsKey) })
        {
            if (valeur.Length > 0 && !File.Exists(Environment.ExpandEnvironmentVariables(valeur)))
                throw new ConfigException($"[server] {champ} : fichier introuvable ({valeur})");
        }
        if (Server.MaxAuthFailures < 1)
            throw new ConfigException("[server] max_auth_failures doit valoir au moins 1");
        if (Server.AuthLockoutSeconds < 1)
            throw new ConfigException("[server] auth_lockout_seconds doit valoir au moins 1");
        if (!ValidPositions.Contains(Overlay.Position))
            throw new ConfigException($"[overlay] position doit etre parmi {string.Join(", ", ValidPositions)}");
        if (Overlay.Opacity is < 0.0 or > 1.0)
            throw new ConfigException("[overlay] opacity doit etre compris entre 0.0 et 1.0");
        if (Overlay.TextOpacity is < 0.05 or > 1.0)
            throw new ConfigException("[overlay] text_opacity doit etre compris entre 0.05 et 1.0");
        if (Overlay.Columns < 1)
            throw new ConfigException("[overlay] columns doit valoir au moins 1");
        if (Overlay.FontSize is < 6 or > 72)
            throw new ConfigException("[overlay] font_size doit etre compris entre 6 et 72");
    }

    /// <summary>Lecture typee d'une table TOML, avec suivi des cles non reconnues.</summary>
    private sealed class SectionReader(string name, TomlTable table, List<string> warnings)
    {
        private readonly HashSet<string> _seen = [];

        public bool Bool(string key, bool fallback)
        {
            _seen.Add(key);
            if (!table.TryGetValue(key, out var raw)) return fallback;
            return raw is bool b ? b : throw new ConfigException($"[{name}] {key} attend un booleen");
        }

        public int Int(string key, int fallback)
        {
            _seen.Add(key);
            if (!table.TryGetValue(key, out var raw)) return fallback;
            return raw switch
            {
                long l => checked((int)l),
                int i => i,
                double d when Math.Abs(d - Math.Round(d)) < double.Epsilon => (int)d,
                _ => throw new ConfigException($"[{name}] {key} attend un nombre entier"),
            };
        }

        public double Double(string key, double fallback)
        {
            _seen.Add(key);
            if (!table.TryGetValue(key, out var raw)) return fallback;
            return raw switch
            {
                double d => d,
                long l => l,
                int i => i,
                _ => throw new ConfigException($"[{name}] {key} attend un nombre"),
            };
        }

        public string String(string key, string fallback)
        {
            _seen.Add(key);
            if (!table.TryGetValue(key, out var raw)) return fallback;
            return raw is string s ? s : throw new ConfigException($"[{name}] {key} attend une chaine");
        }

        public List<string> StringList(string key, List<string> fallback)
        {
            _seen.Add(key);
            if (!table.TryGetValue(key, out var raw)) return fallback;
            if (raw is not TomlArray array || array.Any(item => item is not string))
            {
                throw new ConfigException($"[{name}] {key} attend une liste de chaines");
            }
            return array.Select(item => (string)item!).ToList();
        }

        public void Ignore(string key, string reason)
        {
            _seen.Add(key);
            if (table.ContainsKey(key))
            {
                warnings.Add($"[{name}] {key} ignore : {reason}");
            }
        }

        public void Finish()
        {
            foreach (var key in table.Keys)
            {
                if (!_seen.Contains(key))
                {
                    warnings.Add($"[{name}] option inconnue ignoree : {key}");
                }
            }
        }
    }
}
