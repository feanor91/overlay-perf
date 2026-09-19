using System.Text.Json.Nodes;

namespace OverlayPerf.Models;

/// <summary>Famille de materiel a laquelle se rattache une mesure.</summary>
public enum Group
{
    Cpu,
    Gpu,
    Memory,
    Fan,
    Fps,
    Storage,
    Network,
    System,
}

/// <summary>Nature physique d'une mesure, utilisee pour le rendu (jauge, couleur, unite).</summary>
public enum Kind
{
    Temperature,
    Load,
    Frequency,
    Memory,
    Fan,
    Power,
    Voltage,
    Fps,
    Duration,
    Rate,
    /// <summary>Valeur textuelle (nom de processus...) plutot que numerique : voir <see cref="Reading.Text"/>.</summary>
    Text,
    /// <summary>Facteur sans dimension (multiplicateur de generation d'images...).</summary>
    Multiplier,
}

public static class EnumNames
{
    /// <summary>Identifiants publies dans l'API : identiques a la version Python
    /// pour que l'application mobile existante continue de fonctionner sans changement.</summary>
    public static string Wire(this Group group) => group switch
    {
        Group.Cpu => "cpu",
        Group.Gpu => "gpu",
        Group.Memory => "memory",
        Group.Fan => "fan",
        Group.Fps => "fps",
        Group.Storage => "storage",
        Group.Network => "network",
        _ => "system",
    };

    public static string Wire(this Kind kind) => kind switch
    {
        Kind.Temperature => "temperature",
        Kind.Load => "load",
        Kind.Frequency => "frequency",
        Kind.Memory => "memory",
        Kind.Fan => "fan",
        Kind.Power => "power",
        Kind.Voltage => "voltage",
        Kind.Fps => "fps",
        Kind.Duration => "duration",
        Kind.Rate => "rate",
        Kind.Text => "text",
        _ => "multiplier",
    };
}

/// <summary>
/// Une mesure unitaire produite par un backend de capteurs.
/// <para><see cref="Key"/> est un identifiant stable (<c>cpu.temp</c>, <c>gpu.0.fan</c>) :
/// c'est lui qui sert de selecteur dans la configuration de l'overlay et de l'application mobile.</para>
/// </summary>
public sealed record Reading
{
    /// <summary>Bornes par defaut utilisees pour dessiner une jauge quand le capteur n'en fournit pas.</summary>
    private static readonly Dictionary<Kind, (double Low, double High)> DefaultRanges = new()
    {
        [Kind.Temperature] = (20.0, 100.0),
        [Kind.Load] = (0.0, 100.0),
        [Kind.Fan] = (0.0, 3000.0),
        [Kind.Fps] = (0.0, 240.0),
        [Kind.Duration] = (0.0, 50.0),
        [Kind.Frequency] = (0.0, 6000.0),
        [Kind.Memory] = (0.0, 32768.0),
        [Kind.Power] = (0.0, 500.0),
        [Kind.Voltage] = (0.0, 2.0),
        [Kind.Rate] = (0.0, 100.0),
    };

    /// <summary>Mesures pour lesquelles une jauge a un sens : ailleurs, l'echelle haute est
    /// arbitraire et les clients se contentent d'afficher la valeur numerique.</summary>
    private static readonly HashSet<Kind> GaugeKinds =
        [Kind.Temperature, Kind.Load, Kind.Fan, Kind.Memory, Kind.Power, Kind.Fps];

    public required string Key { get; init; }
    public required string Label { get; init; }
    public double? Value
    {
        get => _value;
        // Un capteur absent renvoie parfois NaN/inf : on le normalise en "pas de valeur".
        init => _value = value is { } v && double.IsFinite(v) ? v : null;
    }
    private readonly double? _value;
    /// <summary>Valeur textuelle pour <see cref="Kind.Text"/> (nom de processus...), en plus ou
    /// a la place de <see cref="Value"/> : la mesure "a une valeur" des que l'un des deux est pose.</summary>
    public string? Text { get; init; }
    public string Unit { get; init; } = "";
    public required Group Group { get; init; }
    public required Kind Kind { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public string Source { get; init; } = "";
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = EmptyExtra;

    private static readonly IReadOnlyDictionary<string, object?> EmptyExtra = new Dictionary<string, object?>();

    /// <summary>Bornes d'affichage, en completant avec les valeurs par defaut du <see cref="Kind"/>.</summary>
    public (double Low, double High) Range
    {
        get
        {
            var (low, high) = DefaultRanges.TryGetValue(Kind, out var r) ? r : (0.0, 100.0);
            return (Minimum ?? low, Maximum ?? high);
        }
    }

    public bool IsGauge => GaugeKinds.Contains(Kind);

    public JsonObject ToJson()
    {
        var (low, high) = Range;
        var node = new JsonObject
        {
            ["key"] = Key,
            ["label"] = Label,
            ["value"] = Value,
            ["text"] = Text,
            ["unit"] = Unit,
            ["group"] = Group.Wire(),
            ["kind"] = Kind.Wire(),
            ["min"] = low,
            ["max"] = high,
            ["gauge"] = IsGauge,
            ["source"] = Source,
        };
        if (Extra.Count > 0)
        {
            var extra = new JsonObject();
            foreach (var (k, v) in Extra)
            {
                extra[k] = v is null ? null : JsonValue.Create(v.ToString());
            }
            node["extra"] = extra;
        }
        return node;
    }
}
