using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;

namespace OverlayPerf.Sensors;

/// <summary>Backend de demonstration : valeurs plausibles sans materiel (reglage de l'affichage, demo, CI).</summary>
public sealed class MockBackend : SensorBackend
{
    private readonly Random _random;
    private readonly double _period;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public MockBackend(ILogger logger, int? seed = null, double period = 30.0) : base(logger)
    {
        _random = seed is { } s ? new Random(s) : new Random();
        _period = period;
    }

    public override string Name => "mock";
    public override string Description => "Valeurs simulees (demo et tests, aucun materiel requis)";

    private double Wave(double baseline, double amplitude, double phase)
    {
        var angle = 2 * Math.PI * (_clock.Elapsed.TotalSeconds / _period) + phase;
        var noise = (_random.NextDouble() * 2 - 1) * amplitude * 0.08;
        return baseline + amplitude * Math.Sin(angle) + noise;
    }

    public override IReadOnlyList<Reading> Read()
    {
        var cpuLoad = Math.Clamp(Wave(45, 35, 0.0), 0, 100);
        var gpuLoad = Math.Clamp(Wave(60, 35, 1.1), 0, 100);
        var specs = new (string Key, string Label, double Value, string Unit, Group Group, Kind Kind, double? Low, double? High)[]
        {
            ("cpu.load", "CPU", cpuLoad, "%", Group.Cpu, Kind.Load, 0.0, 100.0),
            ("cpu.temp", "CPU temperature", 38 + cpuLoad * 0.45, "°C", Group.Cpu, Kind.Temperature, null, 95.0),
            ("cpu.clock", "Frequence CPU", Wave(4200, 500, 0.3), "MHz", Group.Cpu, Kind.Frequency, 800.0, 5200.0),
            ("cpu.power", "CPU consommation", 22 + cpuLoad * 1.05, "W", Group.Cpu, Kind.Power, 0.0, 142.0),
            ("gpu.0.load", "GPU charge", gpuLoad, "%", Group.Gpu, Kind.Load, 0.0, 100.0),
            ("gpu.0.temp", "GPU temperature", 40 + gpuLoad * 0.4, "°C", Group.Gpu, Kind.Temperature, null, 90.0),
            ("gpu.0.fan", "GPU ventilateur", 25 + gpuLoad * 0.5, "%", Group.Gpu, Kind.Load, 0.0, 100.0),
            ("gpu.0.power", "GPU consommation", 60 + gpuLoad * 1.6, "W", Group.Gpu, Kind.Power, 0.0, 250.0),
            ("gpu.0.vram.used", "GPU VRAM", Wave(6000, 1800, 2.0), "MiB", Group.Gpu, Kind.Memory, 0.0, 12282.0),
            ("memory.load", "RAM", Math.Clamp(Wave(52, 12, 2.4), 0, 100), "%", Group.Memory, Kind.Load, 0.0, 100.0),
            ("memory.used", "RAM utilisee", Wave(17000, 3000, 2.4), "MiB", Group.Memory, Kind.Memory, 0.0, 32768.0),
            ("fan.cpu", "Ventilateur CPU", Wave(1200, 350, 0.6), "RPM", Group.Fan, Kind.Fan, 0.0, 2200.0),
            ("fan.boitier.1", "Ventilateur boitier 1", Wave(900, 200, 1.4), "RPM", Group.Fan, Kind.Fan, 0.0, 1800.0),
            ("fan.boitier.2", "Ventilateur boitier 2", Wave(880, 200, 2.8), "RPM", Group.Fan, Kind.Fan, 0.0, 1800.0),
        };
        return specs.Select(s => new Reading
        {
            Key = s.Key, Label = s.Label, Value = Math.Round(s.Value, 1), Unit = s.Unit,
            Group = s.Group, Kind = s.Kind, Minimum = s.Low, Maximum = s.High, Source = Name,
        }).ToList();
    }
}
