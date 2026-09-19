using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OverlayPerf.Fps;

/// <summary>
/// FPS via PresentMon (Intel, open source) : ecoute les evenements ETW de presentation
/// DXGI/D3D/Vulkan, donc mesure tous les jeux, y compris en plein ecran exclusif, sans hook.
/// <para>Le processus PresentMon est lance en sous-processus, sa sortie CSV lue ligne a ligne.
/// Sa sortie d'erreur est conservee et journalisee : c'est elle qui explique un arret
/// immediat (option inconnue, session ETW deja prise, droits insuffisants), la ou l'agent
/// Python la jetait et laissait FPS a « — » sans un mot.</para>
/// </summary>
public sealed class PresentMonSource : IDisposable
{
    /// <summary>Processus qui presentent en permanence sans etre des jeux, utilises comme filet
    /// de securite tant qu'aucune application au premier plan n'a ete identifiee comme cible
    /// (voir <see cref="ForegroundProcess"/>) : sans cette liste, le tout premier instant apres
    /// le demarrage — avant la premiere verification du premier plan — melangerait les trames
    /// de l'Explorateur, du Gestionnaire des taches ou d'un navigateur a celles du jeu.</summary>
    public static readonly string[] DefaultExcludes =
    [
        "explorer.exe", "dwm.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "SearchHost.exe",
        "StartMenuExperienceHost.exe", "ShellExperienceHost.exe", "LockApp.exe", "Widgets.exe",
        "Taskmgr.exe", "SystemSettings.exe", "ScreenClippingHost.exe", "SnippingTool.exe",
        "msedgewebview2.exe", "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe",
        "Discord.exe", "steamwebhelper.exe", "Spotify.exe", "Code.exe", "WindowsTerminal.exe",
        "NVIDIA Overlay.exe", "GameBar.exe", "GameBarFTServer.exe", "Teams.exe", "ms-teams.exe",
        "OverlayPerf.exe", "LibreHardwareMonitor.exe", "PresentMon.exe",
    ];

    /// <summary>Frequence de verification de l'application au premier plan : assez rapide pour
    /// suivre un alt-tab vers un nouveau jeu, assez espace pour ne pas relancer PresentMon en
    /// boucle si l'utilisateur bascule frequemment de fenetre.</summary>
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];

    private readonly FrameTimeTracker _tracker;
    private readonly ILogger _log;
    private readonly IReadOnlyList<string> _excludes;
    private readonly string? _fixedTarget;
    private readonly Func<string?> _detectForeground;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private System.Threading.Timer? _foregroundTimer;
    private Process? _process;
    private volatile bool _retargeting;
    /// <summary>Mutable : desactive automatiquement si PresentMon ne reconnait pas
    /// <c>--track_frame_type</c> (option beta, absente des versions anterieures).</summary>
    private bool _trackFrameType;

    /// <summary>Application actuellement ciblee via <c>--process_name</c> (<c>null</c> = aucune
    /// identifiee pour l'instant, repli sur la liste noire <see cref="_excludes"/>).</summary>
    private string? _target;

    public PresentMonSource(FrameTimeTracker tracker, ILogger log, string executable,
        IReadOnlyList<string>? excludes = null, string? processFilter = null, Func<string?>? detectForeground = null,
        bool trackFrameGeneration = false)
    {
        _tracker = tracker;
        _log = log;
        Executable = executable;
        _excludes = excludes ?? DefaultExcludes;
        // Un appelant qui impose un processus precis (tests, futur usage) desactive la
        // detection automatique : cette cible ne change plus jamais.
        _fixedTarget = string.IsNullOrWhiteSpace(processFilter) ? null : processFilter.Trim();
        _target = _fixedTarget;
        _detectForeground = detectForeground ?? ForegroundProcess.Name;
        _trackFrameType = trackFrameGeneration;
    }

    public string Executable { get; }
    public string Name => "presentmon";

    /// <summary>Application actuellement ciblee (voir <see cref="_target"/>), pour diagnostic
    /// (fenetre Etat…, journaux) ; <c>null</c> tant qu'aucune n'a ete identifiee.</summary>
    public string? CurrentTarget => _target;

    /// <summary>Nombre de trames lues depuis le demarrage, pour la surveillance.</summary>
    public long FramesRead => Interlocked.Read(ref _framesRead);
    private long _framesRead;

    /// <summary>Le processus PresentMon tourne actuellement.</summary>
    public bool Running => _process is { HasExited: false };

    /// <summary>Signale un echec definitif (message deja explicite), a afficher a l'utilisateur.</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        if (_thread is not null) return;
        if (_fixedTarget is null)
        {
            // Premiere lecture synchrone : autant lancer PresentMon deja cible sur le bon
            // processus des le premier essai plutot que d'attendre le premier tic du minuteur.
            _target = ForegroundCandidate();
            _foregroundTimer = new System.Threading.Timer(_ => PollForeground(), null, ForegroundPollInterval, ForegroundPollInterval);
        }
        _thread = new Thread(Run) { Name = "fps-presentmon", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Application au premier plan si elle est un candidat plausible (ni <c>null</c>,
    /// ni dans la liste noire) ; sinon <c>null</c>.</summary>
    private string? ForegroundCandidate() => LegitimateCandidate(_detectForeground(), _excludes);

    /// <summary>Un nom de processus au premier plan est un candidat plausible pour PresentMon
    /// s'il est connu (l'appel Win32 peut echouer) et absent de la liste noire. Fonction pure,
    /// testable sans lancer ni fenetre ni processus.</summary>
    public static string? LegitimateCandidate(string? foreground, IReadOnlyList<string> excludes) =>
        foreground is not null && !excludes.Contains(foreground, StringComparer.OrdinalIgnoreCase) ? foreground : null;

    /// <summary>Appele par le minuteur : aligne la cible de PresentMon sur l'application
    /// reellement au premier plan, y compris pour revenir a <c>null</c> (aucune mesure) des que
    /// ce premier plan cesse d'etre un jeu plausible. Ne jamais garder une cible perimee : c'est
    /// exactement ce qui laissait un ancien processus mesure indefiniment (bureau, Explorateur...)
    /// et affichait un FPS sans rapport avec ce que l'utilisateur regarde.</summary>
    private void PollForeground()
    {
        var candidate = ForegroundCandidate();
        if (string.Equals(candidate, _target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _log.LogInformation("Nouvelle cible PresentMon : {Target}", candidate ?? "aucune (plus de jeu au premier plan)");
        _target = candidate;
        var process = _process;
        if (process is { HasExited: false })
        {
            // _retargeting ne doit etre pose que si on tue reellement un PresentMon en cours :
            // sinon (aucune cible precedente, PresentMon pas encore lance), le drapeau resterait
            // pose a tort et masquerait un vrai echec au tout premier lancement de la cible.
            _retargeting = true;
            try { process.Kill(entireProcessTree: true); } catch { /* deja termine entre-temps */ }
        }
    }

    /// <summary>Attente entre deux verifications quand aucune cible n'est identifiee, pour
    /// reagir vite des qu'une application legitime prend le premier plan.</summary>
    private static readonly TimeSpan NoTargetPollInterval = TimeSpan.FromMilliseconds(500);

    private void Run()
    {
        var attempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            if (_target is null)
            {
                // Bureau, ecran de verrouillage, application exclue au premier plan... : pas de
                // jeu identifie, donc pas de mesure plutot qu'un balayage aveugle de tout le
                // systeme (c'est exactement ce balayage qui affichait un FPS errone au bureau).
                _stop.Token.WaitHandle.WaitOne(NoTargetPollInterval);
                continue;
            }
            var (exitCode, stderr, framesThisRun) = RunOnce();
            if (_stop.IsCancellationRequested)
            {
                break;
            }
            if (_retargeting)
            {
                // Redemarrage volontaire pour changer de cible : ni un echec, ni une raison
                // d'attendre. On y va tout de suite, avec le compteur d'essais remis a zero.
                _retargeting = false;
                attempt = 0;
                continue;
            }
            // Sortie spontanee : PresentMon ne s'arrete jamais de lui-meme en fonctionnement normal.
            var errorLine = stderr.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase));
            var tail = errorLine ?? (stderr.Count == 0 ? "(aucune sortie d'erreur)" : string.Join(" | ", stderr.TakeLast(3)));

            // --track_frame_type est une option beta absente des versions anterieures de
            // PresentMon : une option qu'il ne reconnait pas ne doit pas casser tout le FPS,
            // juste desactiver ce comptage precis (retente aussitot, sans le drapeau).
            if (_trackFrameType && framesThisRun == 0 && stderr.Any(IsUnknownOptionError))
            {
                _log.LogWarning("PresentMon ne reconnait pas --track_frame_type (version trop ancienne ?) : "
                                 + "multiplicateur de generation d'images desactive pour cette session. {Stderr}", tail);
                _trackFrameType = false;
                attempt = 0;
                continue;
            }

            // Refus deterministe : relancer ne changera rien, autant le dire tout de suite.
            if (framesThisRun == 0 && stderr.Any(IsPrivilegeError))
            {
                var message = "PresentMon refuse de demarrer faute de droits administrateur (" + tail + "). "
                              + "Relancez OverlayPerf via l'invite UAC : sans elevation, aucune trame ne sera jamais recue.";
                _log.LogError("{Message}", message);
                Failed?.Invoke(message);
                return;
            }
            if (attempt >= RestartDelays.Length)
            {
                var message = $"PresentMon s'arrete a repetition (code {exitCode}) : abandon. Derniere sortie : {tail}";
                _log.LogError("{Message}", message);
                Failed?.Invoke(message);
                return;
            }
            var delay = RestartDelays[attempt];
            _log.LogWarning("PresentMon termine (code {Code}) apres {Frames} trames : {Stderr}. Relance dans {Delay} s",
                exitCode, framesThisRun, tail, delay.TotalSeconds);
            // Un arret immediat sans la moindre trame est une erreur de lancement, pas un
            // incident passager : on le dit tout de suite, sans attendre les cinq relances.
            if (attempt == 0 && framesThisRun == 0 && stderr.Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)))
            {
                Failed?.Invoke($"PresentMon refuse de demarrer : {stderr.First(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))}");
            }
            attempt++;
            if (_stop.Token.WaitHandle.WaitOne(delay))
            {
                break;
            }
        }
    }

    private (int ExitCode, List<string> Stderr, long Frames) RunOnce()
    {
        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Pas de --no_top : supprime en PresentMon 2.x ("unrecognized option", sortie immediate).
        // Inutile de toute facon : stdout etant un tube, PresentMon (1.x comme 2.x) desactive
        // lui-meme son affichage console. Pas de --v2_metrics non plus : inconnu de 1.x.
        psi.ArgumentList.Add("--output_stdout");
        psi.ArgumentList.Add("--session_name");
        psi.ArgumentList.Add("OverlayPerf");
        psi.ArgumentList.Add("--stop_existing_session");
        // Run() ne lance jamais PresentMon sans cible legitime identifiee : plus de repli sur
        // un balayage "--exclude" de tout le systeme (voir Run()).
        var target = _target ?? throw new InvalidOperationException("RunOnce appele sans cible");
        psi.ArgumentList.Add("--process_name");
        psi.ArgumentList.Add(target);
        if (_trackFrameType)
        {
            psi.ArgumentList.Add("--track_frame_type");
        }

        var stderr = new List<string>();
        long frames = 0;
        Process process;
        try
        {
            _log.LogInformation("Lancement de PresentMon : {Exe} (cible unique : {Target})", Executable, target);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start a renvoye null");
        }
        catch (Exception ex)
        {
            var message = $"PresentMon introuvable ou impossible a lancer ({Executable}) : {ex.Message}";
            _log.LogError(ex, "{Message}", message);
            Failed?.Invoke(message);
            _stop.Cancel();
            return (-1, stderr, 0);
        }
        _process = process;

        // stderr est vide par un thread dedie : une aide de 10 Ko remplirait le tube et
        // bloquerait PresentMon avant qu'il ne quitte, et nous avec lui.
        var stderrThread = new Thread(() =>
        {
            try
            {
                while (process.StandardError.ReadLine() is { } line)
                {
                    if (line.Trim().Length == 0) continue;
                    lock (stderr)
                    {
                        if (stderr.Count < 50) stderr.Add(line.Trim());
                    }
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                        _log.LogWarning("PresentMon stderr : {Line}", line.Trim());
                    else
                        _log.LogDebug("PresentMon stderr : {Line}", line.Trim());
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Lecture stderr PresentMon interrompue");
            }
        }) { IsBackground = true, Name = "fps-presentmon-stderr" };
        stderrThread.Start();

        var parser = new PresentMonCsv(_tracker, header =>
            _log.LogWarning("En-tete PresentMon sans colonne de temps de trame : {Header}", header));
        var headerLogged = false;
        try
        {
            while (!_stop.IsCancellationRequested && process.StandardOutput.ReadLine() is { } line)
            {
                if (parser.Feed(line))
                {
                    frames++;
                    if (Interlocked.Increment(ref _framesRead) == 1)
                    {
                        _log.LogInformation("Premiere trame recue de PresentMon ({App})", ExtractApp(parser, line));
                    }
                }
                else if (!headerLogged && parser.Header is { } header)
                {
                    headerLogged = true;
                    _log.LogInformation("En-tete PresentMon : {Header}", string.Join(",", header));
                }
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Lecture du flux PresentMon interrompue");
        }

        if (!process.WaitForExit(3000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* deja termine */ }
            process.WaitForExit(2000);
        }
        stderrThread.Join(1000);
        var code = process.HasExited ? process.ExitCode : -1;
        process.Dispose();
        _process = null;
        lock (stderr)
        {
            return (code, stderr.ToList(), frames);
        }
    }

    private static bool IsPrivilegeError(string line) =>
        line.Contains("access denied", StringComparison.OrdinalIgnoreCase)
        || line.Contains("administrative privileges", StringComparison.OrdinalIgnoreCase)
        || line.Contains("elevated privilege", StringComparison.OrdinalIgnoreCase) && line.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknownOptionError(string line) =>
        line.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
        || line.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
        || line.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not recognized", StringComparison.OrdinalIgnoreCase);

    private static string ExtractApp(PresentMonCsv parser, string line)
    {
        var header = parser.Header;
        if (header is null) return "?";
        var idx = Array.FindIndex(header.ToArray(), h => PresentMonCsv.ApplicationColumns.Contains(h.ToLowerInvariant()));
        var cells = line.Split(',');
        return idx >= 0 && idx < cells.Length ? cells[idx] : "?";
    }

    public void Stop()
    {
        _stop.Cancel();
        _foregroundTimer?.Dispose();
        _foregroundTimer = null;
        var process = _process;
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Arret de PresentMon");
            }
        }
        _thread?.Join(3000);
        _thread = null;
    }

    public void Dispose() => Stop();

    // --- Localisation de l'executable ------------------------------------------

    /// <summary>
    /// Cherche PresentMon : chemin configure, puis a cote de l'executable, puis sur le PATH.
    /// Retourne aussi la liste de ce qui a ete essaye, pour un message d'erreur utile.
    /// </summary>
    public static (string? Path, List<string> Tried) Locate(string configuredPath)
    {
        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!System.IO.Path.IsPathRooted(expanded))
            {
                expanded = System.IO.Path.Combine(AppContext.BaseDirectory, expanded);
            }
            tried.Add(expanded);
            return (File.Exists(expanded) ? expanded : null, tried);
        }

        // A cote de l'executable : fichier telecharge tel quel (PresentMon-2.5.1-x64.exe).
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory, "PresentMon*.exe")
            .Where(f => !System.IO.Path.GetFileName(f).Contains("Capture", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        tried.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "PresentMon*.exe"));
        if (local is not null)
        {
            return (local, tried);
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "PresentMon.exe", "presentmon.exe" })
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                {
                    tried.Add(candidate);
                    return (candidate, tried);
                }
            }
        }
        tried.Add("PresentMon.exe sur le PATH");
        return (null, tried);
    }
}
