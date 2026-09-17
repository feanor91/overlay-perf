using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;

namespace OverlayPerf.Sensors;

/// <summary>
/// Temperatures, ventilateurs, puissances, frequences et GPU via la bibliotheque
/// LibreHardwareMonitor, integree a l'executable.
/// <para>Contrairement a l'agent Python, qui interrogeait l'application LibreHardwareMonitor
/// par HTTP (a lancer soi-meme, en administrateur, avec le serveur web active), tout se passe
/// ici dans le processus : les droits administrateur exiges par le manifeste suffisent a
/// charger le pilote noyau qui lit les sondes.</para>
/// </summary>
public sealed class LibreHardwareBackend : SensorBackend
{
    /// <summary>Intitules exacts des sondes "processeur entier", par ordre de preference
    /// (verifies dans le code source de LibreHardwareMonitor : AmdCpu.cs et IntelCpu.cs).</summary>
    private static readonly string[] CpuTempPriority =
    [
        "CPU Package",      // Intel : temperature du boitier entier.
        "Core (Tctl/Tdie)", // AMD Ryzen (Zen 2+) : capteur combine, le plus courant.
        "Core (Tctl)",      // AMD Ryzen (Zen/Zen+) : cible de regulation des ventilateurs.
        "Core (Tdie)",      // AMD Ryzen : die reel, legerement sous Tctl.
        "Core Max",         // Intel, repli : maximum instantane entre coeurs.
        "Core Average",     // Intel, repli : moyenne entre coeurs.
    ];

    private static readonly string[] CpuPowerPriority =
    [
        "CPU Package", // Intel : somme coeurs + cache + controleur memoire integre.
        "Package",     // AMD Ryzen : consommation du boitier entier (capteur SMU).
    ];

    private static readonly Dictionary<SensorType, (Kind Kind, string Unit)> TypeMap = new()
    {
        [SensorType.Temperature] = (Kind.Temperature, "°C"),
        [SensorType.Load] = (Kind.Load, "%"),
        [SensorType.Fan] = (Kind.Fan, "RPM"),
        [SensorType.Control] = (Kind.Load, "%"),
        [SensorType.Clock] = (Kind.Frequency, "MHz"),
        [SensorType.Power] = (Kind.Power, "W"),
        [SensorType.Voltage] = (Kind.Voltage, "V"),
        [SensorType.Data] = (Kind.Memory, "GiB"),
        [SensorType.SmallData] = (Kind.Memory, "MiB"),
        [SensorType.Throughput] = (Kind.Rate, "MiB/s"),
    };

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly bool _perCore;
    private bool _opened;

    public LibreHardwareBackend(ILogger logger, bool perCore = false) : base(logger)
    {
        _perCore = perCore;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
            IsPsuEnabled = true,
            IsNetworkEnabled = false,
            IsBatteryEnabled = false,
        };
    }

    public override string Name => "lhm";
    public override string Description => "LibreHardwareMonitor (integre) : temperatures, ventilateurs, puissances, GPU";
    public override bool Available => _opened;

    /// <summary>Cartes NVIDIA vues par LibreHardwareMonitor (via NVML) : quand il y en a, le
    /// secours nvidia-smi est inutile et ne ferait que publier des cles gpu.N.* concurrentes.</summary>
    public int NvidiaGpuCount => _opened ? _computer.Hardware.Count(h => h.HardwareType == HardwareType.GpuNvidia) : 0;

    /// <summary>Ordre de publication des GPU : la carte dediee d'abord (gpu.0), les puces
    /// integrees ensuite. Sans cela, sur un Ryzen avec partie graphique integree, gpu.0 designait
    /// la puce integree inactive et l'overlay affichait 0 % de charge pendant que la vraie carte chauffait.</summary>
    private List<IHardware> OrderedGpus() =>
        _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .Select(h => (Hardware: h, Integrated: IsIntegrated(h), Vram: VramTotal(h)))
            .OrderBy(x => x.Integrated ? 1 : 0)
            .ThenByDescending(x => x.Vram)
            .Select(x => x.Hardware)
            .ToList();

    private static bool IsIntegrated(IHardware gpu)
    {
        var name = gpu.Name;
        if (gpu.HardwareType == HardwareType.GpuIntel && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            return true; // UHD, Iris, HD Graphics : integres
        }
        if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || (name.Contains(" Vega ", StringComparison.OrdinalIgnoreCase) && name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)))
        {
            return true; // APU AMD
        }
        // Une carte dediee a rarement moins de 3 Gio de memoire propre.
        var vram = VramTotal(gpu);
        return vram > 0 && vram < 3000;
    }

    private static double VramTotal(IHardware gpu)
    {
        var flat = Flatten(gpu).ToList();
        return ToDouble(Find(flat, SensorType.SmallData, "GPU Memory Total")?.Value)
               ?? ToDouble(Find(flat, SensorType.SmallData, "D3D Dedicated Memory Total")?.Value)
               ?? 0;
    }

    /// <summary>Charge le pilote et enumere le materiel. Quelques secondes au premier appel.</summary>
    public bool Open()
    {
        if (_opened)
        {
            return true;
        }
        var chrono = Stopwatch.StartNew();
        try
        {
            _computer.Open();
            _computer.Accept(_visitor);
            _opened = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "LibreHardwareMonitor : ouverture impossible (pilote noyau refuse ?)");
            return false;
        }

        var hardware = _computer.Hardware;
        Logger.LogInformation("LibreHardwareMonitor pret en {Elapsed} ms : {Count} composants",
            chrono.ElapsedMilliseconds, hardware.Count);
        foreach (var hw in hardware)
        {
            var sensors = CountSensors(hw);
            Logger.LogInformation("  - {Type,-18} {Name} ({Sensors} sondes)", hw.HardwareType, hw.Name, sensors);
        }
        var gpus = OrderedGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            Logger.LogInformation("GPU gpu.{Index} = {Name}{Integrated}", i, gpus[i].Name, IsIntegrated(gpus[i]) ? " (integre)" : "");
        }
        if (hardware.All(h => h.HardwareType != HardwareType.Cpu))
        {
            Logger.LogWarning("Aucun processeur vu par LibreHardwareMonitor : le pilote WinRing0/PawnIO "
                + "n'a probablement pas pu se charger (antivirus, Windows en mode S, machine virtuelle).");
        }
        return true;
    }

    private static int CountSensors(IHardware hw) => hw.Sensors.Length + hw.SubHardware.Sum(CountSensors);

    public override IReadOnlyList<Reading> Read()
    {
        if (!_opened)
        {
            return [];
        }
        _computer.Accept(_visitor);

        var readings = new List<Reading>();
        var seen = new HashSet<string>();
        var gpuIndex = 0;
        var gpus = OrderedGpus();
        var ordered = _computer.Hardware.Where(h => !gpus.Contains(h)).Concat(gpus);

        foreach (var hw in ordered)
        {
            var flat = Flatten(hw).ToList();
            // Catalogue brut : chaque sonde sous une cle stable `lhm.<materiel>.<type>.<nom>`.
            foreach (var (owner, sensor) in flat)
            {
                if (!TypeMap.TryGetValue(sensor.SensorType, out var map))
                {
                    continue;
                }
                var value = ToDouble(sensor.Value);
                var key = $"lhm.{SensorText.Slug(hw.Name)}.{SensorText.Slug(sensor.SensorType.ToString())}.{SensorText.Slug(sensor.Name)}";
                if (!seen.Add(key))
                {
                    var suffix = 2;
                    while (!seen.Add($"{key}.{suffix}"))
                    {
                        suffix++;
                    }
                    key = $"{key}.{suffix}";
                }
                readings.Add(new Reading
                {
                    Key = key,
                    Label = $"{hw.Name} {sensor.Name}".Trim(),
                    Value = value,
                    Unit = map.Unit,
                    Group = Classify(hw.HardwareType, sensor),
                    Kind = map.Kind,
                    Maximum = map.Kind == Kind.Temperature ? ToDouble(sensor.Max) : null,
                    Source = Name,
                    Extra = new Dictionary<string, object?>
                    {
                        ["hardware"] = hw.Name,
                        ["sensorId"] = sensor.Identifier.ToString(),
                        ["text"] = sensor.Name,
                        ["owner"] = owner.Name,
                    },
                });
            }

            // Alias stables attendus par l'overlay et l'application mobile.
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    readings.AddRange(CpuAliases(flat));
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    readings.AddRange(GpuAliases(hw, flat, gpuIndex));
                    gpuIndex++;
                    break;
                case HardwareType.Motherboard:
                case HardwareType.SuperIO:
                case HardwareType.Cooler:
                case HardwareType.EmbeddedController:
                    readings.AddRange(FanAliases(hw, flat));
                    break;
                case HardwareType.Storage:
                    readings.AddRange(StorageAliases(hw, flat));
                    break;
            }
        }
        return readings;
    }

    // --- Alias -------------------------------------------------------------------

    private IEnumerable<Reading> CpuAliases(List<(IHardware Owner, ISensor Sensor)> flat)
    {
        // Sans acces au pilote noyau, LibreHardwareMonitor renvoie 0 plutot que null : un
        // processeur a 0 degre ou 0 W n'existe pas, on prefere afficher un tiret.
        var temp = Pick(flat, SensorType.Temperature, CpuTempPriority);
        if (temp is { } t && ToDouble(t.Value) is { } tv && tv > 0)
        {
            yield return new Reading
            {
                Key = "cpu.temp", Label = "CPU temperature", Value = tv, Unit = "°C",
                Group = Group.Cpu, Kind = Kind.Temperature, Maximum = ToDouble(t.Max) is { } m && m > tv ? Math.Max(m, 95) : 100.0,
                Source = Name, Extra = new Dictionary<string, object?> { ["alias_de"] = t.Name },
            };
        }
        var power = Pick(flat, SensorType.Power, CpuPowerPriority);
        if (power is { } p && ToDouble(p.Value) is { } pv && pv > 0)
        {
            yield return new Reading
            {
                Key = "cpu.power", Label = "CPU consommation", Value = pv, Unit = "W",
                Group = Group.Cpu, Kind = Kind.Power, Minimum = 0.0, Maximum = 250.0,
                Source = Name, Extra = new Dictionary<string, object?> { ["alias_de"] = p.Name },
            };
        }
        var clocks = flat.Where(x => x.Sensor.SensorType == SensorType.Clock
                                      && x.Sensor.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
                                      && !x.Sensor.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase))
                         .Select(x => ToDouble(x.Sensor.Value)).OfType<double>().Where(v => v > 0).ToList();
        if (clocks.Count > 0)
        {
            yield return new Reading
            {
                Key = "cpu.clock", Label = "Frequence CPU", Value = Math.Round(clocks.Average(), 0), Unit = "MHz",
                Group = Group.Cpu, Kind = Kind.Frequency, Minimum = 0.0, Maximum = Math.Max(6000.0, clocks.Max()),
                Source = Name,
            };
        }
        if (_perCore)
        {
            foreach (var (_, sensor) in flat.Where(x => x.Sensor.SensorType == SensorType.Load
                                                        && x.Sensor.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)))
            {
                if (!int.TryParse(sensor.Name["CPU Core #".Length..].Split(' ')[0], out var n) || ToDouble(sensor.Value) is not { } lv)
                {
                    continue;
                }
                yield return new Reading
                {
                    Key = $"cpu.core.{n - 1}.load", Label = $"Coeur {n - 1}", Value = lv, Unit = "%",
                    Group = Group.Cpu, Kind = Kind.Load, Source = Name,
                };
            }
        }
    }

    private IEnumerable<Reading> GpuAliases(IHardware hw, List<(IHardware Owner, ISensor Sensor)> flat, int index)
    {
        var prefix = $"gpu.{index}";
        var name = SensorText.ShortGpuName(hw.Name);
        var extra = new Dictionary<string, object?> { ["device"] = hw.Name };

        Reading? Make(string suffix, string label, double? value, string unit, Kind kind, double? low, double? high)
        {
            if (value is null) return null;
            return new Reading
            {
                Key = $"{prefix}.{suffix}", Label = $"{name} {label}", Value = Math.Round(value.Value, 1), Unit = unit,
                Group = Group.Gpu, Kind = kind, Minimum = low, Maximum = high, Source = Name, Extra = extra,
            };
        }

        var load = Find(flat, SensorType.Load, "GPU Core");
        var temp = Find(flat, SensorType.Temperature, "GPU Core") ?? Find(flat, SensorType.Temperature, "GPU Temperature");
        var hotspot = Find(flat, SensorType.Temperature, "GPU Hot Spot");
        var fanPct = Find(flat, SensorType.Control, "GPU Fan") ?? Find(flat, SensorType.Control, "GPU Fan 1");
        var fanRpm = Find(flat, SensorType.Fan, "GPU Fan") ?? Find(flat, SensorType.Fan, "GPU Fan 1") ?? Find(flat, SensorType.Fan, "GPU");
        var vramUsed = Find(flat, SensorType.SmallData, "GPU Memory Used") ?? Find(flat, SensorType.SmallData, "D3D Dedicated Memory Used");
        var vramTotal = Find(flat, SensorType.SmallData, "GPU Memory Total") ?? Find(flat, SensorType.SmallData, "D3D Dedicated Memory Total");
        var vramLoad = Find(flat, SensorType.Load, "GPU Memory") ?? Find(flat, SensorType.Load, "GPU Memory Controller");
        var power = Find(flat, SensorType.Power, "GPU Package") ?? Find(flat, SensorType.Power, "GPU Power")
                    ?? Find(flat, SensorType.Power, "GPU Core") ?? flat.FirstOrDefault(x => x.Sensor.SensorType == SensorType.Power).Sensor;
        var clockCore = Find(flat, SensorType.Clock, "GPU Core");
        var clockMem = Find(flat, SensorType.Clock, "GPU Memory");

        var candidates = new[]
        {
            Make("load", "charge", ToDouble(load?.Value), "%", Kind.Load, 0.0, 100.0),
            Make("temp", "temperature", Positive(ToDouble(temp?.Value)), "°C", Kind.Temperature, null, 95.0),
            Make("hotspot", "point chaud", Positive(ToDouble(hotspot?.Value)), "°C", Kind.Temperature, null, 110.0),
            fanPct is not null
                ? Make("fan", "ventilateur", ToDouble(fanPct.Value), "%", Kind.Load, 0.0, 100.0)
                : Make("fan", "ventilateur", ToDouble(fanRpm?.Value), "RPM", Kind.Fan, 0.0, 3500.0),
            Make("vram.used", "VRAM", ToDouble(vramUsed?.Value), "MiB", Kind.Memory, 0.0, ToDouble(vramTotal?.Value)),
            Make("vram.load", "bus memoire", ToDouble(vramLoad?.Value), "%", Kind.Load, 0.0, 100.0),
            Make("power", "consommation", ToDouble(power?.Value), "W", Kind.Power, 0.0, 450.0),
            Make("clock.core", "frequence", ToDouble(clockCore?.Value), "MHz", Kind.Frequency, 0.0, null),
            Make("clock.mem", "frequence VRAM", ToDouble(clockMem?.Value), "MHz", Kind.Frequency, 0.0, null),
        };
        foreach (var r in candidates)
        {
            if (r is not null)
            {
                yield return r;
            }
        }

        // Vitesse reelle des ventilateurs (tours/minute), en plus du pourcentage ci-dessus :
        // « GPU Fan » seul, ou « GPU Fan 1 », « GPU Fan 2 »... sur les cartes a plusieurs
        // ventilateurs. Le premier est gpu.N.fan.rpm, les suivants gpu.N.fan.2.rpm, etc.
        var rpmFans = flat.Where(x => x.Sensor.SensorType == SensorType.Fan
                                       && x.Sensor.Name.StartsWith("GPU Fan", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(x => x.Sensor.Name, StringComparer.OrdinalIgnoreCase)
                          .Select(x => x.Sensor)
                          .ToList();
        for (var i = 0; i < rpmFans.Count; i++)
        {
            var value = ToDouble(rpmFans[i].Value);
            if (value is null) continue;
            var suffix = i == 0 ? "fan.rpm" : $"fan.{i + 1}.rpm";
            var label = i == 0 && rpmFans.Count == 1 ? "ventilateur (RPM)" : $"ventilateur {i + 1} (RPM)";
            yield return new Reading
            {
                Key = $"{prefix}.{suffix}", Label = $"{name} {label}", Value = Math.Round(value.Value, 0), Unit = "RPM",
                Group = Group.Gpu, Kind = Kind.Fan, Minimum = 0.0, Maximum = Math.Max(3500.0, ToDouble(rpmFans[i].Max) ?? 0),
                Source = Name, Extra = extra,
            };
        }
    }

    private IEnumerable<Reading> FanAliases(IHardware hw, List<(IHardware Owner, ISensor Sensor)> flat)
    {
        foreach (var (owner, sensor) in flat.Where(x => x.Sensor.SensorType == SensorType.Fan))
        {
            if (ToDouble(sensor.Value) is not { } value)
            {
                continue;
            }
            var label = sensor.Name;
            // "Fan #1" sur une carte mere n'aide pas : on prefixe par le composant quand c'est un
            // controleur externe (AIO, boitier) pour distinguer les groupes de ventilateurs.
            if (hw.HardwareType is HardwareType.Cooler or HardwareType.EmbeddedController)
            {
                label = $"{SensorText.ShortGpuName(hw.Name)} {sensor.Name}";
            }
            yield return new Reading
            {
                Key = $"fan.{SensorText.Slug(owner.Name)}.{SensorText.Slug(sensor.Name)}",
                Label = label,
                Value = value,
                Unit = "RPM",
                Group = Group.Fan,
                Kind = Kind.Fan,
                Minimum = 0.0,
                Maximum = Math.Max(3000.0, ToDouble(sensor.Max) ?? 0),
                Source = Name,
                Extra = new Dictionary<string, object?> { ["hardware"] = hw.Name, ["text"] = sensor.Name },
            };
        }
    }

    private IEnumerable<Reading> StorageAliases(IHardware hw, List<(IHardware Owner, ISensor Sensor)> flat)
    {
        var temp = Find(flat, SensorType.Temperature, "Temperature") ?? flat.FirstOrDefault(x => x.Sensor.SensorType == SensorType.Temperature).Sensor;
        if (temp is null || ToDouble(temp.Value) is not { } value)
        {
            yield break;
        }
        yield return new Reading
        {
            Key = $"temp.{SensorText.Slug(hw.Name)}",
            Label = hw.Name,
            Value = value,
            Unit = "°C",
            Group = Group.Storage,
            Kind = Kind.Temperature,
            Maximum = 70.0,
            Source = Name,
            Extra = new Dictionary<string, object?> { ["hardware"] = hw.Name },
        };
    }

    // --- Outils ------------------------------------------------------------------

    private static IEnumerable<(IHardware Owner, ISensor Sensor)> Flatten(IHardware hw)
    {
        foreach (var sensor in hw.Sensors)
        {
            yield return (hw, sensor);
        }
        foreach (var sub in hw.SubHardware)
        {
            foreach (var item in Flatten(sub))
            {
                yield return item;
            }
        }
    }

    private static ISensor? Find(List<(IHardware Owner, ISensor Sensor)> flat, SensorType type, string name) =>
        flat.FirstOrDefault(x => x.Sensor.SensorType == type
                                 && string.Equals(x.Sensor.Name, name, StringComparison.OrdinalIgnoreCase)).Sensor;

    private static ISensor? Pick(List<(IHardware Owner, ISensor Sensor)> flat, SensorType type, string[] priority)
    {
        foreach (var name in priority)
        {
            if (Find(flat, type, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static double? ToDouble(float? value) =>
        value is { } v && float.IsFinite(v) ? Math.Round(v, 2) : null;

    private static double? Positive(double? value) => value is { } v && v > 0 ? v : null;

    private static Group Classify(HardwareType type, ISensor sensor)
    {
        switch (type)
        {
            case HardwareType.Cpu:
                return Group.Cpu;
            case HardwareType.GpuNvidia:
            case HardwareType.GpuAmd:
            case HardwareType.GpuIntel:
                return Group.Gpu;
            case HardwareType.Memory:
                return Group.Memory;
            case HardwareType.Storage:
                return Group.Storage;
            case HardwareType.Network:
                return Group.Network;
            case HardwareType.Cooler:
                return Group.Fan;
        }
        if (sensor.SensorType is SensorType.Fan or SensorType.Control)
        {
            return Group.Fan;
        }
        return Group.System;
    }

    public override void Dispose()
    {
        if (_opened)
        {
            try
            {
                _computer.Close();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Fermeture LibreHardwareMonitor en echec");
            }
            _opened = false;
        }
    }

    /// <summary>Rafraichit toutes les sondes, sous-composants compris.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
