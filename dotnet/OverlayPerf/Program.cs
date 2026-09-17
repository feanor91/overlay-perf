using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using OverlayPerf.App;
using OverlayPerf.Config;
using OverlayPerf.Logging;
using OverlayPerf.Models;
using OverlayPerf.Server;
using OverlayPerf.Ui;

namespace OverlayPerf;

public static class Program
{
    private const string Usage = """
        OverlayPerf — overlay de monitoring materiel et serveur pour l'application mobile.

        Usage : OverlayPerf.exe [options]

          (sans option)      overlay + serveur mobile, icone dans la zone de notification
          --no-overlay       ne pas afficher l'overlay
          --no-server        ne pas demarrer le serveur
          --config <chemin>  fichier de configuration TOML (defaut : %LOCALAPPDATA%\overlay\config.toml)
          --config-init      creer un fichier de configuration d'exemple puis quitter
          --sensors          lister les capteurs detectes et un instantane, puis quitter
          --pair             afficher l'adresse et le QR code a scanner, puis quitter
          --mock             valeurs simulees (demonstration, aucun materiel requis)
          --no-elevate       ne pas proposer la relance en administrateur (diagnostic)
          --version          afficher la version
          --help             cette aide

        Les droits administrateur sont demandes au lancement (invite UAC) : PresentMon et le
        pilote de LibreHardwareMonitor en ont besoin. Journaux : %LOCALAPPDATA%\overlay\logs.
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Error is { } error)
        {
            WithConsole(() => Console.Error.WriteLine($"Option invalide : {error}\n\n{Usage}"));
            return 2;
        }
        if (options.Help)
        {
            WithConsole(() => Console.WriteLine(Usage));
            return 0;
        }
        if (options.Version)
        {
            WithConsole(() => Console.WriteLine($"OverlayPerf {WebServer.Version}"));
            return 0;
        }
        if (options.ConfigInit)
        {
            return WithConsole(CreateConfig);
        }

        AppConfig config;
        try
        {
            config = AppConfig.Load(options.ConfigPath);
            if (options.Mock) config.General.Mock = true;
        }
        catch (Exception ex) when (ex is ConfigException or Tomlyn.TomlException or IOException or InvalidOperationException or OverflowException)
        {
            var message = $"Configuration illisible ({options.ConfigPath ?? Paths.ConfigFile}) :\n{ex.Message}";
            if (!WithConsole(() => Console.Error.WriteLine(message), onlyIfAttached: true))
            {
                MessageBox.Show(message, "OverlayPerf", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 2;
        }

        var consoleAttached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        if (consoleAttached)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
            Console.WriteLine();
        }
        using var logs = LogSetup.Create(config, consoleAttached);
        var log = logs.CreateLogger("overlay");

        try
        {
            if (options.Sensors) return RunSensors(config, logs);
            if (options.Pair) return RunPair(config);
            return RunApp(config, options, logs, log, consoleAttached);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Arret sur erreur non geree");
            if (!consoleAttached)
            {
                MessageBox.Show($"OverlayPerf s'est arrete sur une erreur :\n{ex.Message}\n\nDetails dans {LogSetup.CurrentLogFile}",
                    "OverlayPerf", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
        finally
        {
            LogSetup.Flush();
            if (consoleAttached) NativeMethods.FreeConsole();
        }
    }

    // --- Application principale ------------------------------------------------------

    private static int RunApp(AppConfig config, Options options, ILoggerFactory logs, ILogger log, bool consoleAttached)
    {
        // Une seule instance : deux PresentMon se disputeraient la session ETW, deux serveurs le port.
        using var mutex = new Mutex(true, @"Global\OverlayPerf.SingleInstance", out var first);
        if (!first)
        {
            log.LogWarning("Une autre instance tourne deja : arret");
            MessageBox.Show("OverlayPerf est deja lance : regardez l'icone dans la zone de notification.",
                "OverlayPerf", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        log.LogInformation("==== OverlayPerf {Version} — demarrage ====", WebServer.Version);
        log.LogInformation("Executable : {Exe}", Environment.ProcessPath);
        log.LogInformation("Windows {Os}, .NET {Runtime}, {Cpu} coeurs logiques", Environment.OSVersion.VersionString,
            Environment.Version, Environment.ProcessorCount);
        log.LogInformation("Configuration : {Path} ({State})", options.ConfigPath ?? Paths.ConfigFile,
            File.Exists(options.ConfigPath ?? Paths.ConfigFile) ? "chargee" : "absente, valeurs par defaut");
        foreach (var warning in config.Warnings)
        {
            log.LogWarning("Configuration : {Warning}", warning);
        }

        if (!IsAdministrator())
        {
            // Le manifeste exige l'elevation : n'arrive que si l'executable a ete lance d'une
            // maniere qui l'ignore (certains lanceurs, compatibilite). On propose de relancer.
            log.LogWarning("Le processus ne tourne pas en administrateur : PresentMon ne transmettrait aucune trame");
            if (!consoleAttached && !options.NoElevate && TryRelaunchElevated(log))
            {
                return 0;
            }
            log.LogWarning("Poursuite sans droits administrateur : FPS et temperatures seront probablement absents");
        }
        else
        {
            log.LogInformation("Droits administrateur : oui");
        }

        var withOverlay = !options.NoOverlay && config.Overlay.Enabled;
        var withServer = !options.NoServer && config.Server.Enabled;
        if (!withOverlay && !withServer)
        {
            log.LogError("Rien a lancer : overlay et serveur sont tous deux desactives");
            return 1;
        }

        ApplicationConfiguration.Initialize();
        using var runtime = new Runtime(config, logs, needToken: withServer);
        foreach (var warning in runtime.Warnings)
        {
            log.LogWarning("{Warning}", warning.Replace('\n', ' '));
        }

        OverlayForm? overlay = withOverlay ? new OverlayForm(config.Overlay) : null;
        TrayHost? host = null;

        void ToggleOverlay()
        {
            if (overlay is null) return;
            if (host!.InvokeRequired) host.BeginInvoke(overlay.Toggle);
            else overlay.Toggle();
        }

        void ShowPairing()
        {
            var summary = Pairing.Summary(config.Server.Port, runtime.Token, config.Server.PublicUrl,
                runtime.Server?.Scheme ?? "http");
            using var dialog = new PairingDialog(summary);
            dialog.ShowDialog();
        }

        void Tick()
        {
            if (overlay is null || !overlay.Visible) return;
            if (runtime.Hub.Latest is { } latest)
            {
                overlay.Apply(latest);
                overlay.ReassertTopMost();
            }
        }

        host = new TrayHost(config, logs.CreateLogger("overlay.ui"), ToggleOverlay,
            withServer ? ShowPairing : null, Tick, runtime.StatusText);
        runtime.Problem += message =>
        {
            log.LogError("{Message}", message);
            host.Notify("OverlayPerf — probleme", message, ToolTipIcon.Error);
        };

        runtime.Start(withServer);

        if (overlay is not null)
        {
            var hotkeyOk = host.RegisterHotkey(config.Overlay.Hotkey, ToggleOverlay);
            if (config.Overlay.VisibleAtStart)
            {
                overlay.Show();
            }
            else if (!hotkeyOk)
            {
                log.LogWarning("Overlay masque au demarrage et raccourci indisponible : utilisez l'icone de notification pour l'afficher");
            }
        }
        host.RegisterHotkey(config.Overlay.HotkeyQuit, () => host.BeginInvoke(Application.Exit));

        if (consoleAttached)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                host.BeginInvoke(Application.Exit);
            };
        }

        if (runtime.Server is not null)
        {
            var summary = Pairing.Summary(config.Server.Port, runtime.Token, config.Server.PublicUrl, runtime.Server.Scheme);
            log.LogInformation("Telephone (reseau local) : {Url}", summary.LocalUrl);
            if (summary.RemoteUrl.Length > 0) log.LogInformation("Telephone (a distance) : {Url}", summary.RemoteUrl);
            if (consoleAttached)
            {
                Console.WriteLine($"Serveur Overlay sur {runtime.Server.Scheme}://{summary.Addresses[0]}:{config.Server.Port}/");
                Console.WriteLine($"Telephone (reseau local) : {summary.LocalUrl}");
                Console.WriteLine(Pairing.RenderQrText(summary.PrimaryUrl));
            }
        }

        // Bilan de demarrage dans la zone de notification : la seule sortie visible sans terminal.
        var bilan = runtime.FrameSource is { } src
            ? $"FPS via {Path.GetFileName(src.Executable)} ; capteurs : {string.Join(", ", runtime.Hub.Backends.Select(b => b.Name))}."
            : $"Sans source FPS locale ; capteurs : {string.Join(", ", runtime.Hub.Backends.Select(b => b.Name))}.";
        if (runtime.Warnings.Count > 0)
        {
            host.Notify("OverlayPerf demarre avec des reserves", runtime.Warnings[0].Split('\n')[0] + " (details dans le journal)");
        }
        else
        {
            host.Notify("OverlayPerf actif", bilan, ToolTipIcon.Info);
        }

        // Verification differee de la source FPS : assez tard pour ne pas confondre "pas de jeu
        // lance pour l'instant" avec une source reellement cassee.
        var watchdog = new System.Windows.Forms.Timer { Interval = 30_000 };
        watchdog.Tick += (_, _) => { watchdog.Stop(); runtime.CheckFrameSource(); };
        watchdog.Start();

        Application.ApplicationExit += (_, _) => log.LogInformation("Arret demande");
        Application.Run(host);

        watchdog.Dispose();
        overlay?.Dispose();
        host.Dispose();
        runtime.Dispose();
        log.LogInformation("==== OverlayPerf arrete ====");
        return 0;
    }

    // --- Commandes console -------------------------------------------------------------

    private static int RunSensors(AppConfig config, ILoggerFactory logs)
    {
        using var runtime = new Runtime(config, logs, needToken: false);
        foreach (var warning in runtime.Warnings)
        {
            Console.Error.WriteLine($"\nAttention : {warning}");
        }
        // Deux lectures : les debits et la charge CPU sont des deltas entre deux appels.
        runtime.Hub.Poll();
        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(1.0, config.General.PollInterval)));
        var snapshot = runtime.Hub.Poll();

        Console.WriteLine($"Machine : {snapshot.Host}");
        Console.WriteLine($"Administrateur : {(IsAdministrator() ? "oui" : "non")}");
        Console.WriteLine("Backends :");
        foreach (var backend in runtime.Hub.Backends)
        {
            Console.WriteLine($"  - {backend.Name,-10} {(backend.Available ? "actif" : "indisponible"),-13} {backend.Description}");
        }
        if (snapshot.Readings.Count == 0)
        {
            Console.WriteLine("\nAucune mesure disponible.");
            return 1;
        }
        Console.WriteLine($"\n{snapshot.Readings.Count} mesures :");
        Models.Group? current = null;
        foreach (var reading in snapshot.Readings.OrderBy(r => r.Group.Wire()).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            if (reading.Group != current)
            {
                current = reading.Group;
                Console.WriteLine($"\n  [{current.Value.Wire()}]");
            }
            var value = reading.Value is { } v ? $"{v:0.##} {reading.Unit}" : "—";
            Console.WriteLine($"    {reading.Key,-44} {value,16}   {reading.Label}");
        }
        return 0;
    }

    private static int RunPair(AppConfig config)
    {
        var token = Paths.ResolveToken(config);
        var scheme = config.Server.TlsCert.Length > 0 ? "https" : "http";
        var summary = Pairing.Summary(config.Server.Port, token, config.Server.PublicUrl, scheme);
        Console.WriteLine(summary.RemoteUrl.Length > 0
            ? "Ouvrez cette adresse sur le telephone, depuis n'importe quel reseau :\n"
            : "Ouvrez cette adresse sur le telephone, sur le meme reseau local :\n");
        Console.WriteLine($"  {summary.PrimaryUrl}\n");
        if (summary.RemoteUrl.Length > 0)
        {
            Console.WriteLine($"Sur place, l'adresse locale evite le detour par Internet :\n  {summary.LocalUrl}\n");
        }
        else if (summary.Urls.Count > 1)
        {
            Console.WriteLine("Autres adresses possibles :");
            foreach (var url in summary.Urls.Skip(1)) Console.WriteLine($"  {url}");
            Console.WriteLine();
        }
        Console.WriteLine(Pairing.RenderQrText(summary.PrimaryUrl));
        Console.WriteLine($"\nJeton : {token}");
        Console.WriteLine("\nCe jeton donne acces a la telemetrie de la machine : ne le diffusez pas.");
        return 0;
    }

    private static int CreateConfig()
    {
        if (File.Exists(Paths.ConfigFile))
        {
            Console.Error.WriteLine($"{Paths.ConfigFile} existe deja : rien n'a ete ecrit.");
            return 1;
        }
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(Paths.ConfigFile, ConfigTemplate.Text);
        Console.WriteLine($"Configuration creee : {Paths.ConfigFile}");
        return 0;
    }

    // --- Outils --------------------------------------------------------------------------

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return NativeMethods.IsUserAnAdmin();
        }
    }

    private static bool TryRelaunchElevated(ILogger log)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        // Executable unique : args[0] est OverlayPerf.exe, a ne pas repasser. Lance via
        // « dotnet OverlayPerf.dll » (developpement) : args[0] est la DLL, a conserver.
        var commandLine = Environment.GetCommandLineArgs();
        var passthrough = string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? commandLine
            : commandLine.Skip(1);
        var answer = MessageBox.Show(
            "OverlayPerf a besoin des droits administrateur (PresentMon et lecture des sondes materielles).\n\n"
            + "Relancer en administrateur maintenant ?",
            "OverlayPerf", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', passthrough.Select(a => $"\"{a}\"")),
            });
            log.LogInformation("Relance en administrateur demandee");
            return true;
        }
        catch (Exception ex)
        {
            // L'utilisateur a refuse l'invite UAC.
            log.LogWarning(ex, "Relance en administrateur refusee");
            return false;
        }
    }

    /// <summary>Execute une commande console : attache la console parente si l'exe a ete lance depuis un terminal.</summary>
    private static bool WithConsole(Action action, bool onlyIfAttached = false)
    {
        var attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        if (!attached && onlyIfAttached) return false;
        try
        {
            if (attached)
            {
                // Le curseur de la console est apres l'invite : on repart sur une ligne propre.
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
                Console.WriteLine();
            }
            action();
            return true;
        }
        finally
        {
            if (attached) NativeMethods.FreeConsole();
        }
    }

    private static int WithConsole(Func<int> action)
    {
        var code = 0;
        WithConsole(() => code = action());
        return code;
    }

    private sealed record Options(
        string? ConfigPath, bool NoOverlay, bool NoServer, bool Sensors, bool Pair, bool ConfigInit,
        bool Mock, bool Version, bool Help, bool NoElevate, string? Error)
    {
        private static Options Invalid(string error) =>
            new(null, false, false, false, false, false, false, false, false, false, error);

        public static Options Parse(string[] args)
        {
            string? configPath = null;
            bool noOverlay = false, noServer = false, sensors = false, pair = false, init = false, mock = false,
                version = false, help = false, noElevate = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config":
                        if (i + 1 >= args.Length) return Invalid("--config attend un chemin");
                        configPath = args[++i];
                        break;
                    case "--no-overlay": noOverlay = true; break;
                    case "--no-server": noServer = true; break;
                    case "--sensors" or "sensors": sensors = true; break;
                    case "--pair" or "pair": pair = true; break;
                    case "--config-init": init = true; break;
                    case "--mock": mock = true; break;
                    case "--no-elevate": noElevate = true; break;
                    case "--version" or "-v": version = true; break;
                    case "--help" or "-h" or "/?": help = true; break;
                    case "run" or "overlay" or "serve":
                        // Sous-commandes de l'agent Python, acceptees pour la transition.
                        if (args[i] == "overlay") noServer = true;
                        if (args[i] == "serve") noOverlay = true;
                        break;
                    default:
                        return Invalid(args[i]);
                }
            }
            return new Options(configPath, noOverlay, noServer, sensors, pair, init, mock, version, help, noElevate, null);
        }
    }
}
