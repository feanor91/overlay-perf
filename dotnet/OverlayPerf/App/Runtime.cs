using System.Text;
using Microsoft.Extensions.Logging;
using OverlayPerf.Config;
using OverlayPerf.Fps;
using OverlayPerf.Hub;
using OverlayPerf.Sensors;
using OverlayPerf.Server;

namespace OverlayPerf.App;

/// <summary>Composants vivants d'une session : capteurs, suivi FPS, hub, serveur.</summary>
public sealed class Runtime : IDisposable
{
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;

    public Runtime(AppConfig config, ILoggerFactory logs, bool needToken)
    {
        Config = config;
        _logs = logs;
        _log = logs.CreateLogger("overlay");

        var backends = SensorDetection.Detect(config, logs, Warnings);

        if (config.Fps.Mode != "off")
        {
            Tracker = new FrameTimeTracker(config.Fps.WindowSeconds);
            // Le FPS est place en tete : c'est la mesure la plus regardee, et l'ordre des
            // backends fixe l'ordre d'affichage par defaut.
            backends.Insert(0, new FpsBackend(Tracker, logs.CreateLogger("overlay.fps")));
            FrameSource = BuildFrameSource(config, Tracker);
        }

        Hub = new MetricsHub(backends, logs.CreateLogger("overlay.hub"), config.General.PollInterval, config.General.HistorySize);
        Token = needToken ? Paths.ResolveToken(config) : config.Server.Token;
    }

    public AppConfig Config { get; }
    public MetricsHub Hub { get; }
    public FrameTimeTracker? Tracker { get; }
    public PresentMonSource? FrameSource { get; }
    public WebServer? Server { get; private set; }
    public string Token { get; }

    /// <summary>Avertissements actionnables collectes au demarrage (source FPS absente, pilote refuse...).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Signale un probleme survenu apres le demarrage (PresentMon mort, port occupe...).</summary>
    public event Action<string>? Problem;

    private PresentMonSource? BuildFrameSource(AppConfig config, FrameTimeTracker tracker)
    {
        var mode = config.Fps.Mode;
        if (mode == "push")
        {
            _log.LogInformation("Source FPS : API HTTP uniquement (mode push)");
            return null;
        }
        var (path, tried) = PresentMonSource.Locate(config.Fps.PresentMonPath);
        if (path is null)
        {
            var message = mode == "presentmon"
                ? "PresentMon introuvable : indiquez son chemin dans [fps] presentmon_path."
                : "Aucune source de FPS trouvee : FPS, temps de trame et 1 % low resteront absents.\n"
                  + "  Telechargez PresentMon (https://github.com/GameTechDev/PresentMon/releases), placez\n"
                  + "  PresentMon-<version>-x64.exe a cote de OverlayPerf.exe ou renseignez [fps] presentmon_path.";
            _log.LogWarning("{Message} Chemins essayes : {Tried}", message.Replace('\n', ' '), string.Join(" ; ", tried));
            Warnings.Add(message);
            return null;
        }
        _log.LogInformation("PresentMon : {Path}", path);
        var source = new PresentMonSource(tracker, _logs.CreateLogger("overlay.fps.presentmon"), path,
            trackFrameGeneration: config.Fps.TrackFrameGeneration);
        source.Failed += message => Problem?.Invoke(message);
        return source;
    }

    public void Start(bool withServer)
    {
        Hub.Start();
        FrameSource?.Start();
        if (withServer)
        {
            Server = new WebServer(Config, Hub, Tracker, Token, _logs.CreateLogger("overlay.server"));
            try
            {
                Server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var message = $"Le serveur n'a pas pu demarrer sur le port {Config.Server.Port} : {ex.GetBaseException().Message}. "
                              + "Le telephone ne pourra pas se connecter ; l'overlay fonctionne normalement.";
                _log.LogError(ex, "{Message}", message);
                Server = null;
                Problem?.Invoke(message);
            }
        }
    }

    /// <summary>Un long moment apres le demarrage, verifie qu'une source FPS trouvee et lancee produit des trames.</summary>
    public void CheckFrameSource()
    {
        if (FrameSource is null || Tracker is null) return;
        if (FrameSource.FramesRead > 0)
        {
            _log.LogInformation("Source FPS : {Frames} trames recues, tout va bien", FrameSource.FramesRead);
            return;
        }
        if (!FrameSource.Running)
        {
            _log.LogWarning("Source FPS : PresentMon ne tourne plus et aucune trame n'a ete recue");
            return;
        }
        _log.LogInformation("Source FPS : PresentMon tourne mais aucune trame recue pour l'instant. "
                            + "Normal si aucun jeu n'est lance ; sinon, verifiez que le jeu n'est pas dans la liste d'exclusion.");
    }

    public string StatusText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OverlayPerf {WebServer.Version}");
        sb.AppendLine($"Administrateur : {(Program.IsAdministrator() ? "oui" : "NON")}");
        sb.AppendLine($"Configuration : {Paths.ConfigFile}{(File.Exists(Paths.ConfigFile) ? "" : " (absente, valeurs par defaut)")}");
        sb.AppendLine($"Journaux : {Paths.LogDir}");
        sb.AppendLine();
        sb.AppendLine("Capteurs : " + string.Join(", ", Hub.Backends.Select(b => b.Name)));
        var latest = Hub.Latest;
        sb.AppendLine($"Mesures publiees : {latest?.Readings.Count ?? 0}");
        sb.AppendLine();
        if (FrameSource is { } source)
        {
            sb.AppendLine($"PresentMon : {source.Executable}");
            sb.AppendLine($"  {(source.Running ? "en cours" : "arrete")}, {source.FramesRead} trames recues");
            sb.AppendLine($"  Cible : {source.CurrentTarget ?? "aucune (pas de jeu au premier plan)"}");
            var stats = Tracker!.Stats();
            sb.AppendLine(stats.Fps is { } fps ? $"  FPS actuel : {fps} ({stats.Application})" : "  FPS : aucune trame recente");
        }
        else
        {
            sb.AppendLine(Config.Fps.Mode == "off" ? "FPS desactive" : "FPS : aucune source locale (API HTTP uniquement)");
        }
        sb.AppendLine();
        sb.AppendLine(Server is not null
            ? $"Serveur : {Server.Scheme}://{Config.Server.Host}:{Config.Server.Port}/ ({Hub.SubscriberCount} client(s))"
            : "Serveur : arrete");
        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Avertissements au demarrage :");
            foreach (var w in Warnings) sb.AppendLine("  - " + w.Split('\n')[0]);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { Server?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Arret du serveur"); }
        FrameSource?.Dispose();
        Hub.Dispose();
    }
}
