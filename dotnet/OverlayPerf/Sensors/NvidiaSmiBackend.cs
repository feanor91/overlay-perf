using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;

namespace OverlayPerf.Sensors;

/// <summary>
/// GPU NVIDIA via <c>nvidia-smi</c>, present avec tout pilote NVIDIA.
/// <para>Solution de secours : LibreHardwareMonitor couvre deja les cartes NVIDIA par NVML,
/// plus vite et plus finement. Ce backend ne sert que si le pilote noyau de
/// LibreHardwareMonitor n'a pas pu se charger, pour ne pas perdre aussi le GPU.</para>
/// </summary>
public sealed class NvidiaSmiBackend : SensorBackend
{
    /// <summary>Champs demandes a nvidia-smi, dans l'ordre des colonnes CSV renvoyees.</summary>
    public static readonly string[] QueryFields =
    [
        "index", "name", "temperature.gpu", "utilization.gpu", "utilization.memory",
        "memory.used", "memory.total", "fan.speed", "power.draw", "power.limit",
        "clocks.current.graphics", "clocks.current.memory",
    ];

    private readonly string? _executable;
    private readonly TimeSpan _timeout;

    public NvidiaSmiBackend(ILogger logger, TimeSpan? timeout = null) : base(logger)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        _executable = Locate();
    }

    public override string Name => "nvidia";
    public override string Description => "GPU NVIDIA via nvidia-smi (secours) : charge, temperature, VRAM, consommation";
    public override bool Available => _executable is not null;

    private static string? Locate()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "nvidia-smi.exe"));
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        candidates.Add(Path.Combine(system, "nvidia-smi.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public override IReadOnlyList<Reading> Read()
    {
        if (_executable is null)
        {
            return [];
        }
        var psi = new ProcessStartInfo(_executable)
        {
            ArgumentList = { $"--query-gpu={string.Join(',', QueryFields)}", "--format=csv,noheader,nounits" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            return [];
        }
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try { process.Kill(); } catch { /* deja termine */ }
            throw new TimeoutException("nvidia-smi ne repond pas");
        }
        if (process.ExitCode != 0)
        {
            return [];
        }
        var rows = ParseCsv(output.Result);
        var readings = new List<Reading>();
        foreach (var row in rows)
        {
            readings.AddRange(ToReadings(row, rows.Count));
        }
        return readings;
    }

    /// <summary>Transforme la sortie CSV de nvidia-smi en dictionnaires par GPU ;
    /// "[N/A]" et "[Not Supported]" deviennent <c>null</c>.</summary>
    public static List<Dictionary<string, object?>> ParseCsv(string output)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length != QueryFields.Length) continue;
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < QueryFields.Length; i++)
            {
                row[QueryFields[i]] = QueryFields[i] == "name" ? cells[i] : SensorText.ToDouble(cells[i]);
            }
            rows.Add(row);
        }
        return rows;
    }

    private IEnumerable<Reading> ToReadings(Dictionary<string, object?> row, int total)
    {
        var index = (int)((row["index"] as double?) ?? 0);
        var name = row["name"] as string ?? $"GPU {index}";
        var court = SensorText.ShortGpuName(name);
        var etiquette = total <= 1 ? court : $"{court} #{index}";
        var prefix = $"gpu.{index}";
        var extra = new Dictionary<string, object?> { ["device"] = name };
        double? D(string k) => row.TryGetValue(k, out var v) ? v as double? : null;

        var specs = new (string Key, string Label, double? Value, string Unit, Kind Kind, double? Low, double? High)[]
        {
            ($"{prefix}.load", $"{etiquette} charge", D("utilization.gpu"), "%", Kind.Load, 0.0, 100.0),
            ($"{prefix}.temp", $"{etiquette} temperature", D("temperature.gpu"), "°C", Kind.Temperature, null, 95.0),
            ($"{prefix}.fan", $"{etiquette} ventilateur", D("fan.speed"), "%", Kind.Load, 0.0, 100.0),
            ($"{prefix}.vram.used", $"{etiquette} VRAM", D("memory.used"), "MiB", Kind.Memory, 0.0, D("memory.total")),
            ($"{prefix}.vram.load", $"{etiquette} bus memoire", D("utilization.memory"), "%", Kind.Load, 0.0, 100.0),
            ($"{prefix}.power", $"{etiquette} consommation", D("power.draw"), "W", Kind.Power, 0.0, D("power.limit")),
            ($"{prefix}.clock.core", $"{etiquette} frequence", D("clocks.current.graphics"), "MHz", Kind.Frequency, 0.0, null),
            ($"{prefix}.clock.mem", $"{etiquette} frequence VRAM", D("clocks.current.memory"), "MHz", Kind.Frequency, 0.0, null),
        };
        foreach (var s in specs)
        {
            if (s.Value is null) continue;
            yield return new Reading
            {
                Key = s.Key, Label = s.Label, Value = Math.Round(s.Value.Value, 1), Unit = s.Unit,
                Group = Group.Gpu, Kind = s.Kind, Minimum = s.Low, Maximum = s.High, Source = Name, Extra = extra,
            };
        }
    }
}
