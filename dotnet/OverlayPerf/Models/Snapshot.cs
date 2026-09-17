using System.Text.Json.Nodes;

namespace OverlayPerf.Models;

/// <summary>Etat complet du systeme a un instant donne.</summary>
public sealed class Snapshot
{
    public double Timestamp { get; }
    public string Host { get; }
    public IReadOnlyList<Reading> Readings { get; }

    public Snapshot(IReadOnlyList<Reading> readings, double? timestamp = null, string? host = null)
    {
        Readings = readings;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Host = host ?? Environment.MachineName;
    }

    public Reading? Get(string key) => Readings.FirstOrDefault(r => r.Key == key);

    /// <summary>
    /// Restreint le snapshot a une liste de cles (ordre de <paramref name="keys"/> conserve).
    /// Un element terminant par <c>*</c> agit comme un prefixe (<c>gpu.*</c>).
    /// Une liste vide renvoie le snapshot inchange.
    /// </summary>
    public Snapshot Filter(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return this;
        }
        var selected = new List<Reading>();
        var seen = new HashSet<string>();
        foreach (var pattern in keys)
        {
            var prefix = pattern.EndsWith('*') ? pattern[..^1] : null;
            foreach (var reading in Readings)
            {
                if (seen.Contains(reading.Key))
                {
                    continue;
                }
                var matched = prefix is not null
                    ? reading.Key.StartsWith(prefix, StringComparison.Ordinal)
                    : reading.Key == pattern;
                if (matched)
                {
                    selected.Add(reading);
                    seen.Add(reading.Key);
                }
            }
        }
        return new Snapshot(selected, Timestamp, Host);
    }

    public JsonObject ToJson()
    {
        var readings = new JsonArray();
        foreach (var reading in Readings)
        {
            readings.Add(reading.ToJson());
        }
        return new JsonObject
        {
            ["timestamp"] = Timestamp,
            ["host"] = Host,
            ["readings"] = readings,
        };
    }

    /// <summary>
    /// Fusionne les mesures de plusieurs backends en dedoublonnant par cle.
    /// L'ordre des backends porte la priorite : le premier a publier une cle gagne.
    /// </summary>
    public static List<Reading> Merge(IEnumerable<IReadOnlyList<Reading>> groups)
    {
        var merged = new List<Reading>();
        var seen = new HashSet<string>();
        foreach (var readings in groups)
        {
            foreach (var reading in readings)
            {
                if (seen.Add(reading.Key))
                {
                    merged.Add(reading);
                }
            }
        }
        return merged;
    }
}
