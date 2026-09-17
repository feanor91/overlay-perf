using System.Security.Cryptography;
using System.Text;

namespace OverlayPerf.Server;

/// <summary>
/// Authentification par jeton partage, exige sur chaque route y compris le WebSocket.
/// Le jeton peut arriver via <c>Authorization: Bearer …</c>, l'en-tete <c>X-Overlay-Token</c>
/// ou le parametre d'URL <c>token</c> (seul moyen pour un WebSocket depuis un navigateur).
/// </summary>
public static class TokenAuth
{
    public const string QueryParam = "token";
    public const string Header = "x-overlay-token";

    public static string? Extract(Func<string, string?> header, Func<string, string?> query)
    {
        var authorization = header("authorization") ?? "";
        if (authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization[7..].Trim();
            return bearer.Length > 0 ? bearer : null;
        }
        var custom = header(Header)?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            return custom;
        }
        var fromQuery = query(QueryParam)?.Trim();
        return string.IsNullOrEmpty(fromQuery) ? null : fromQuery;
    }

    /// <summary>Comparaison a temps constant, pour ne pas fuiter le jeton octet par octet.</summary>
    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true; // jeton desactive explicitement dans la configuration
        }
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Adresse du client, en lisant <c>X-Forwarded-For</c> uniquement derriere un proxy declare de confiance.</summary>
    public static string? ClientAddress(string? remote, Func<string, string?> header, bool trustProxy)
    {
        if (trustProxy)
        {
            var forwarded = header("x-forwarded-for") ?? "";
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }
        return remote;
    }
}

/// <summary>
/// Verrouillage par adresse apres des echecs d'authentification repetes : quelques erreurs de
/// frappe espacees ne verrouillent jamais, une salve automatisee si.
/// </summary>
public sealed class AuthThrottle
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockout;
    private readonly int _maxTracked;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<DateTime>> _failures = new();
    private readonly Dictionary<string, DateTime> _lockedUntil = new();

    public AuthThrottle(int maxFailures = 10, TimeSpan? window = null, TimeSpan? lockout = null,
        int maxTracked = 4096, Func<DateTime>? clock = null)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
        _lockout = lockout ?? TimeSpan.FromMinutes(5);
        _maxTracked = maxTracked;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Secondes restantes avant de pouvoir reessayer ; 0 si l'acces est ouvert.</summary>
    public double RetryAfter(string? client)
    {
        if (string.IsNullOrEmpty(client)) return 0;
        var now = _clock();
        lock (_lock)
        {
            if (!_lockedUntil.TryGetValue(client, out var end)) return 0;
            if (end <= now)
            {
                _lockedUntil.Remove(client);
                _failures.Remove(client);
                return 0;
            }
            return (end - now).TotalSeconds;
        }
    }

    public void RecordFailure(string? client)
    {
        if (string.IsNullOrEmpty(client)) return;
        var now = _clock();
        lock (_lock)
        {
            Prune(now);
            var recents = _failures.TryGetValue(client, out var list)
                ? list.Where(t => now - t < _window).ToList()
                : [];
            recents.Add(now);
            _failures[client] = recents;
            if (recents.Count >= _maxFailures)
            {
                _lockedUntil[client] = now + _lockout;
                _failures[client] = [];
            }
        }
    }

    /// <summary>Une authentification reussie efface l'ardoise de cette adresse.</summary>
    public void RecordSuccess(string? client)
    {
        if (string.IsNullOrEmpty(client)) return;
        lock (_lock)
        {
            _failures.Remove(client);
            _lockedUntil.Remove(client);
        }
    }

    /// <summary>Empeche la table de grossir indefiniment sous un balayage d'adresses.</summary>
    private void Prune(DateTime now)
    {
        foreach (var (client, end) in _lockedUntil.ToList())
        {
            if (end <= now) _lockedUntil.Remove(client);
        }
        foreach (var (client, stamps) in _failures.ToList())
        {
            if (stamps.All(t => now - t >= _window)) _failures.Remove(client);
        }
        if (_failures.Count > _maxTracked)
        {
            var surplus = _failures.Count - _maxTracked;
            foreach (var client in _failures.OrderBy(kv => kv.Value.Count > 0 ? kv.Value.Max() : DateTime.MinValue)
                         .Take(surplus).Select(kv => kv.Key).ToList())
            {
                _failures.Remove(client);
            }
        }
    }
}
