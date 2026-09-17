namespace OverlayPerf.Fps;

/// <summary>
/// Lecture du flux CSV de PresentMon, toutes versions.
/// <para>Les versions 1.x et 2.x n'ont ni les memes colonnes ni le meme ordre, et la casse a
/// change en cours de route (<c>msBetweenPresents</c> en 1.x et 2.0-2.2, <c>MsBetweenPresents</c>
/// par defaut depuis 2.3, <c>FrameTime</c> seulement avec <c>--v2_metrics</c>) : on se repere
/// sur l'en-tete, sans tenir compte de la casse, plutot que sur des indices fixes.</para>
/// </summary>
public sealed class PresentMonCsv
{
    /// <summary>Colonnes de duree de trame, par ordre de preference (minuscules).</summary>
    public static readonly string[] FrameTimeColumns = ["frametime", "msbetweenpresents", "msbetweendisplaychange"];
    public static readonly string[] ApplicationColumns = ["application", "processname"];

    private readonly FrameTimeTracker _tracker;
    private readonly Action<string>? _onHeaderWithoutFrameTime;
    private string[]? _header;
    private int _frameColumn = -1;
    private int _appColumn = -1;

    public PresentMonCsv(FrameTimeTracker tracker, Action<string>? onHeaderWithoutFrameTime = null)
    {
        _tracker = tracker;
        _onHeaderWithoutFrameTime = onHeaderWithoutFrameTime;
    }

    /// <summary>En-tete courant (une fois rencontre), pour les journaux.</summary>
    public IReadOnlyList<string>? Header => _header;
    public int FrameCount { get; private set; }

    /// <summary>Traite une ligne ; retourne <c>true</c> si une trame a ete enregistree.</summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        var cells = Split(line);
        if (cells.Length == 0)
        {
            return false;
        }
        var first = cells[0].Trim().ToLowerInvariant();
        if (_header is null || ApplicationColumns.Contains(first))
        {
            _header = cells.Select(c => c.Trim()).ToArray();
            var lowered = _header.Select(c => c.ToLowerInvariant()).ToArray();
            _frameColumn = FrameTimeColumns.Select(name => Array.IndexOf(lowered, name)).FirstOrDefault(i => i >= 0, -1);
            _appColumn = ApplicationColumns.Select(name => Array.IndexOf(lowered, name)).FirstOrDefault(i => i >= 0, -1);
            if (_frameColumn < 0)
            {
                _onHeaderWithoutFrameTime?.Invoke(string.Join(",", _header));
            }
            return false;
        }
        if (_frameColumn < 0 || _frameColumn >= cells.Length)
        {
            return false;
        }
        var frameTime = Sensors.SensorText.ToDouble(cells[_frameColumn]);
        if (frameTime is null || frameTime <= 0)
        {
            return false;
        }
        var application = _appColumn >= 0 && _appColumn < cells.Length ? cells[_appColumn].Trim() : null;
        _tracker.AddFrameTime(frameTime.Value, string.IsNullOrEmpty(application) ? null : application);
        FrameCount++;
        return true;
    }

    /// <summary>Lit un flux complet (tests, fichiers) ; retourne le nombre de trames.</summary>
    public int Consume(TextReader reader, Func<bool>? shouldStop = null)
    {
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (shouldStop?.Invoke() == true) break;
            if (Feed(line)) count++;
        }
        return count;
    }

    /// <summary>Decoupage CSV minimal : PresentMon ne cite jamais ses champs, mais on tolere des guillemets.</summary>
    private static string[] Split(string line)
    {
        if (!line.Contains('"'))
        {
            return line.Split(',');
        }
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }
}
