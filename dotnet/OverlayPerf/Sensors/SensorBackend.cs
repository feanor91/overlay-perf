using Microsoft.Extensions.Logging;
using OverlayPerf.Models;

namespace OverlayPerf.Sensors;

/// <summary>Source de mesures materiel. Synchrone et bloquante : le hub l'execute sur son propre thread.</summary>
public abstract class SensorBackend : IDisposable
{
    /// <summary>Cadence de rappel dans les journaux quand un backend echoue en boucle.</summary>
    private const int LogEvery = 60;
    private int _failures;

    protected SensorBackend(ILogger logger)
    {
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>Identifiant court, utilise dans les journaux et dans <see cref="Reading.Source"/>.</summary>
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>Indique si le backend peut fonctionner sur cette machine.</summary>
    public virtual bool Available => true;

    /// <summary>Retourne les mesures courantes. Peut lever : le hub isole les erreurs.</summary>
    public abstract IReadOnlyList<Reading> Read();

    public virtual void Dispose()
    {
    }

    /// <summary>
    /// <see cref="Read"/> protege : une panne de capteur ne doit jamais tuer la boucle.
    /// La trace complete n'est journalisee qu'a la premiere erreur, puis une ligne de
    /// rappel toutes les <see cref="LogEvery"/> occurrences, pour ne pas noyer les journaux a 1 Hz.
    /// </summary>
    public IReadOnlyList<Reading> SafeRead()
    {
        IReadOnlyList<Reading> readings;
        try
        {
            readings = Read();
        }
        catch (Exception ex)
        {
            _failures++;
            if (_failures == 1)
            {
                Logger.LogWarning(ex, "Backend {Backend} : lecture en echec", Name);
            }
            else if (_failures % LogEvery == 0)
            {
                Logger.LogWarning("Backend {Backend} : {Count} echecs consecutifs ({Message})", Name, _failures, ex.Message);
            }
            return [];
        }
        if (_failures > 0)
        {
            Logger.LogInformation("Backend {Backend} : lecture retablie apres {Count} echecs", Name, _failures);
            _failures = 0;
        }
        return readings;
    }

    public object Describe() => new { name = Name, description = Description, available = Available };
}

public static class SensorText
{
    /// <summary>Normalise un nom de sonde en fragment de cle stable (<c>CPU Package</c> → <c>cpu_package</c>).</summary>
    public static string Slug(string text)
    {
        var chars = text.Trim().Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }
        return slug.Length > 0 ? slug : "unknown";
    }

    /// <summary>
    /// Convertit une valeur brute de capteur en nombre, ou <c>null</c> si illisible
    /// (<c>N/A</c>, <c>[Not Supported]</c>, chaine vide, unite collee a la valeur).
    /// </summary>
    public static double? ToDouble(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        // Valeurs formatees selon la locale : "62,3" en francais, "1,234.5" en anglais.
        if (text.Contains(','))
        {
            text = text.Contains('.') ? text.Replace(",", "") : text.Replace(',', '.');
        }
        var cleaned = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c is '+' or '-' or '.')
            {
                cleaned.Append(c);
            }
            else if (cleaned.Length > 0)
            {
                break;
            }
        }
        var candidate = cleaned.ToString();
        if (candidate is "" or "+" or "-" or "." or "+." or "-.")
        {
            return null;
        }
        return double.TryParse(candidate, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    /// <summary>
    /// "NVIDIA GeForce RTX 4070" → "RTX 4070" : repeter le nom complet sur chaque mesure
    /// alourdirait l'overlay sans rien ajouter des qu'il n'y a qu'une carte.
    /// </summary>
    public static string ShortGpuName(string name)
    {
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = name[prefix.Length..].Trim();
                return rest.Length > 0 ? rest : name;
            }
        }
        return name;
    }
}
