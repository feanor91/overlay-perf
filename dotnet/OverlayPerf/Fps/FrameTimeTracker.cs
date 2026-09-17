using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;
using OverlayPerf.Sensors;

namespace OverlayPerf.Fps;

/// <summary>Instantane des metriques de fluidite.</summary>
public sealed record FpsStats(
    double? Fps,
    double? FrameTimeMs,
    double? Low1Percent,
    double? Low01Percent,
    int FrameCount,
    string? Application,
    bool Stale);

/// <summary>
/// Fenetre glissante d'intervalles entre trames, alimentee par plusieurs threads.
/// <para><c>windowSeconds</c> regle la moyenne affichee (1 s donne un compteur reactif mais
/// stable) ; <c>capacity</c> borne l'historique servant aux centiles bas.</para>
/// </summary>
public sealed class FrameTimeTracker
{
    private readonly object _lock = new();
    private readonly Queue<(double Arrival, double Ms)> _frames = new();
    private readonly int _capacity;
    private readonly double _windowSeconds;
    private readonly double _staleAfter;
    private readonly double _maxFrameTimeMs;
    private readonly Func<double> _clock;
    private double? _lastSourceTimestamp;
    private double? _lastArrival;
    private string? _application;

    public FrameTimeTracker(double windowSeconds = 1.0, int capacity = 2000, double staleAfter = 2.0,
        double maxFrameTimeMs = 1000.0, Func<double>? clock = null)
    {
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        _windowSeconds = windowSeconds;
        _capacity = Math.Max(1, capacity);
        _staleAfter = staleAfter;
        _maxFrameTimeMs = maxFrameTimeMs;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    /// <summary>Enregistre la presentation d'une trame datee de <paramref name="timestamp"/> (secondes,
    /// base de temps quelconque mais croissante) ; l'ecart avec la precedente devient la duree de trame.</summary>
    public void AddFrame(double? timestamp = null, string? application = null)
    {
        var arrival = _clock();
        var source = timestamp ?? arrival;
        lock (_lock)
        {
            if (application is not null) _application = application;
            var previous = _lastSourceTimestamp;
            _lastSourceTimestamp = source;
            _lastArrival = arrival;
            if (previous is null) return;
            var deltaMs = (source - previous.Value) * 1000.0;
            // Ecart nul ou negatif : trame dupliquee, ou horloge qui recule. On repart de
            // cette trame comme nouvelle reference sans rien enregistrer.
            if (deltaMs <= 0) return;
            Record(arrival, deltaMs);
        }
    }

    /// <summary>Enregistre directement une duree de trame en millisecondes.</summary>
    public void AddFrameTime(double frameTimeMs, string? application = null)
    {
        if (frameTimeMs <= 0 || !double.IsFinite(frameTimeMs)) return;
        var arrival = _clock();
        lock (_lock)
        {
            if (application is not null) _application = application;
            _lastArrival = arrival;
            Record(arrival, frameTimeMs);
        }
    }

    /// <summary>Une trame de plus d'une seconde n'est pas une trame lente mais une coupure
    /// (alt-tab, chargement, jeu quitte) : la conserver fausserait durablement les centiles bas.</summary>
    private void Record(double arrival, double frameTimeMs)
    {
        if (frameTimeMs > _maxFrameTimeMs) return;
        _frames.Enqueue((arrival, frameTimeMs));
        while (_frames.Count > _capacity) _frames.Dequeue();
    }

    public void Reset()
    {
        lock (_lock)
        {
            _frames.Clear();
            _lastSourceTimestamp = null;
            _lastArrival = null;
            _application = null;
        }
    }

    public FpsStats Stats()
    {
        var now = _clock();
        List<double> recent;
        List<double> history;
        string? application;
        double? last;
        lock (_lock)
        {
            application = _application;
            last = _lastArrival;
            recent = _frames.Where(f => now - f.Arrival <= _windowSeconds).Select(f => f.Ms).ToList();
            history = _frames.Select(f => f.Ms).ToList();
        }

        var stale = last is null || now - last.Value > _staleAfter;
        if (stale || recent.Count == 0)
        {
            return new FpsStats(null, null, null, null, history.Count, application, true);
        }

        var meanFrameTime = recent.Average();
        history.Sort();
        // Un "1 % low" est l'inverse du 99e centile de duree de trame : il decrit les
        // trames les plus lentes, celles que l'oeil percoit comme des saccades.
        double? low1 = history.Count >= 20 ? 1000.0 / Percentile(history, 0.99) : null;
        double? low01 = history.Count >= 200 ? 1000.0 / Percentile(history, 0.999) : null;
        return new FpsStats(
            Math.Round(1000.0 / meanFrameTime, 1),
            Math.Round(meanFrameTime, 2),
            low1 is { } l1 ? Math.Round(l1, 1) : null,
            low01 is { } l01 ? Math.Round(l01, 1) : null,
            history.Count,
            application,
            false);
    }

    /// <summary>Centile par interpolation lineaire sur une liste deja triee (<paramref name="fraction"/> dans [0, 1]).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) throw new ArgumentException("liste vide", nameof(sorted));
        if (sorted.Count == 1) return sorted[0];
        var position = fraction * (sorted.Count - 1);
        var low = (int)Math.Floor(position);
        var high = Math.Min(low + 1, sorted.Count - 1);
        var weight = position - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }
}

/// <summary>Expose les metriques du tracker sous forme de mesures standard.</summary>
public sealed class FpsBackend(FrameTimeTracker tracker, ILogger logger) : SensorBackend(logger)
{
    public override string Name => "fps";
    public override string Description => "Images par seconde et temps de trame (PresentMon ou API)";

    public override IReadOnlyList<Reading> Read()
    {
        var stats = tracker.Stats();
        var extra = stats.Application is { } app
            ? new Dictionary<string, object?> { ["application"] = app }
            : new Dictionary<string, object?>();
        return
        [
            new Reading { Key = "fps.current", Label = "FPS", Value = stats.Fps, Unit = "FPS", Group = Group.Fps, Kind = Kind.Fps, Minimum = 0.0, Source = Name, Extra = extra },
            new Reading { Key = "fps.frametime", Label = "Temps de trame", Value = stats.FrameTimeMs, Unit = "ms", Group = Group.Fps, Kind = Kind.Duration, Minimum = 0.0, Maximum = 50.0, Source = Name, Extra = extra },
            new Reading { Key = "fps.low1", Label = "1 % low", Value = stats.Low1Percent, Unit = "FPS", Group = Group.Fps, Kind = Kind.Fps, Minimum = 0.0, Source = Name, Extra = extra },
            new Reading { Key = "fps.low01", Label = "0,1 % low", Value = stats.Low01Percent, Unit = "FPS", Group = Group.Fps, Kind = Kind.Fps, Minimum = 0.0, Source = Name, Extra = extra },
        ];
    }
}
