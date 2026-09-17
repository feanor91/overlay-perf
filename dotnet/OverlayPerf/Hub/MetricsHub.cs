using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;
using OverlayPerf.Sensors;

namespace OverlayPerf.Hub;

/// <summary>
/// Boucle d'echantillonnage partagee par l'overlay et le serveur : interroge les backends
/// sur un thread dedie, conserve un historique borne et diffuse chaque snapshot aux abonnes.
/// </summary>
public sealed class MetricsHub : IDisposable
{
    private readonly ILogger _log;
    private readonly TimeSpan _pollInterval;
    private readonly int _historySize;
    private readonly Queue<Snapshot> _history = new();
    private readonly object _historyLock = new();
    private readonly HashSet<Channel<Snapshot>> _subscribers = [];
    private readonly object _subscribersLock = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private Snapshot? _latest;

    public MetricsHub(IReadOnlyList<SensorBackend> backends, ILogger log, double pollIntervalSeconds = 1.0, int historySize = 300)
    {
        if (pollIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds));
        Backends = backends;
        _log = log;
        _pollInterval = TimeSpan.FromSeconds(pollIntervalSeconds);
        _historySize = Math.Max(1, historySize);
    }

    public IReadOnlyList<SensorBackend> Backends { get; }
    public double PollIntervalSeconds => _pollInterval.TotalSeconds;
    public Snapshot? Latest => _latest;

    public int SubscriberCount
    {
        get { lock (_subscribersLock) return _subscribers.Count; }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { Name = "overlay-hub", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        _thread?.Join(5000);
        _thread = null;
        lock (_subscribersLock)
        {
            foreach (var channel in _subscribers) channel.Writer.TryComplete();
            _subscribers.Clear();
        }
        foreach (var backend in Backends)
        {
            try { backend.Dispose(); }
            catch (Exception ex) { _log.LogDebug(ex, "Fermeture du backend {Backend} en echec", backend.Name); }
        }
    }

    /// <summary>Interroge tous les backends dans le thread courant et publie le resultat.</summary>
    public Snapshot Poll()
    {
        var chrono = Stopwatch.StartNew();
        var snapshot = new Snapshot(Snapshot.Merge(Backends.Select(b => b.SafeRead())));
        if (chrono.ElapsedMilliseconds > _pollInterval.TotalMilliseconds)
        {
            _log.LogWarning("Cycle de collecte lent : {Elapsed} ms pour une periode de {Period} ms",
                chrono.ElapsedMilliseconds, _pollInterval.TotalMilliseconds);
        }
        lock (_historyLock)
        {
            _history.Enqueue(snapshot);
            while (_history.Count > _historySize) _history.Dequeue();
        }
        _latest = snapshot;
        Publish(snapshot);
        return snapshot;
    }

    private void Loop()
    {
        _log.LogInformation("Collecte demarree : periode {Period} s, {Count} backends", _pollInterval.TotalSeconds, Backends.Count);
        while (!_stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cycle de collecte en echec");
            }
            // On soustrait la duree de lecture pour garder une cadence reguliere.
            var elapsed = Stopwatch.GetElapsedTime(started);
            var delay = _pollInterval - elapsed;
            if (delay > TimeSpan.Zero && _stop.Token.WaitHandle.WaitOne(delay))
            {
                break;
            }
        }
        _log.LogInformation("Collecte arretee");
    }

    // --- Diffusion ---------------------------------------------------------------

    public ChannelReader<Snapshot> Subscribe(int capacity = 4)
    {
        // Client trop lent (telephone sur un Wi-Fi charge) : on jette la mesure la plus
        // ancienne, un etat temps reel perime n'a pas d'interet.
        var channel = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_subscribersLock) _subscribers.Add(channel);
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<Snapshot> reader)
    {
        lock (_subscribersLock)
        {
            var channel = _subscribers.FirstOrDefault(c => ReferenceEquals(c.Reader, reader));
            if (channel is not null)
            {
                _subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }
    }

    private void Publish(Snapshot snapshot)
    {
        Channel<Snapshot>[] targets;
        lock (_subscribersLock) targets = _subscribers.ToArray();
        foreach (var channel in targets)
        {
            channel.Writer.TryWrite(snapshot);
        }
    }

    // --- Lecture -----------------------------------------------------------------

    public List<Snapshot> History(int? limit = null)
    {
        lock (_historyLock)
        {
            var all = _history.ToList();
            return limit is { } n && n < all.Count ? all.Skip(all.Count - n).ToList() : all;
        }
    }

    public IEnumerable<object> DescribeBackends() => Backends.Select(b => b.Describe());

    public void Dispose() => Stop();
}
