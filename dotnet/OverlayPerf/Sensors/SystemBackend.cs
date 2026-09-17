using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OverlayPerf.Models;

namespace OverlayPerf.Sensors;

/// <summary>
/// Socle toujours disponible, sans pilote ni droits particuliers : charge CPU, memoire,
/// debits disque et reseau. Equivalent du backend psutil de l'agent Python.
/// </summary>
public sealed class SystemBackend : SensorBackend
{
    private const double Mib = 1024.0 * 1024.0;

    private readonly bool _perCore;
    private readonly bool _includeIo;
    private PerformanceCounter? _cpuTotal;
    private PerformanceCounter[] _cpuCores = [];
    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private long _lastTicks;
    private (long In, long Out)? _lastNet;

    public SystemBackend(ILogger logger, bool perCore = false, bool includeIo = true) : base(logger)
    {
        _perCore = perCore;
        _includeIo = includeIo;
        try
        {
            // "% Processor Utility" est la mesure affichee par le Gestionnaire des taches
            // (tient compte de la frequence reelle) ; "% Processor Time" est le repli classique.
            _cpuTotal = TryCounter("Processor Information", "% Processor Utility", "_Total")
                        ?? TryCounter("Processor", "% Processor Time", "_Total");
            if (perCore)
            {
                var count = Environment.ProcessorCount;
                _cpuCores = Enumerable.Range(0, count)
                    .Select(i => TryCounter("Processor", "% Processor Time", i.ToString()))
                    .OfType<PerformanceCounter>()
                    .ToArray();
            }
            if (includeIo)
            {
                _diskRead = TryCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWrite = TryCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            }
            // Premier appel : amorce les compteurs, qui renvoient 0 sinon.
            _cpuTotal?.NextValue();
            foreach (var c in _cpuCores) c.NextValue();
            _diskRead?.NextValue();
            _diskWrite?.NextValue();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Compteurs de performance indisponibles : charge CPU et debits disque absents");
        }
    }

    public override string Name => "system";
    public override string Description => "Charge CPU, memoire, debits disque et reseau (Windows)";

    private PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
            pc.NextValue();
            return pc;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Compteur {Category}\\{Counter}({Instance}) indisponible", category, counter, instance);
            return null;
        }
    }

    public override IReadOnlyList<Reading> Read()
    {
        var readings = new List<Reading>();
        ReadCpu(readings);
        ReadMemory(readings);
        if (_includeIo)
        {
            ReadIo(readings);
        }
        return readings;
    }

    private void ReadCpu(List<Reading> readings)
    {
        if (_cpuTotal is not null)
        {
            var load = Math.Clamp(_cpuTotal.NextValue(), 0f, 100f);
            readings.Add(new Reading
            {
                Key = "cpu.load", Label = "CPU", Value = Math.Round(load, 1), Unit = "%",
                Group = Group.Cpu, Kind = Kind.Load, Source = Name,
            });
        }
        for (var i = 0; i < _cpuCores.Length; i++)
        {
            readings.Add(new Reading
            {
                Key = $"cpu.core.{i}.load", Label = $"Coeur {i}", Value = Math.Round(Math.Clamp(_cpuCores[i].NextValue(), 0f, 100f), 1),
                Unit = "%", Group = Group.Cpu, Kind = Kind.Load, Source = Name,
            });
        }
    }

    private void ReadMemory(List<Reading> readings)
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return;
        }
        var totalMib = status.ullTotalPhys / Mib;
        var usedMib = (status.ullTotalPhys - status.ullAvailPhys) / Mib;
        readings.Add(new Reading
        {
            Key = "memory.load", Label = "RAM", Value = status.dwMemoryLoad, Unit = "%",
            Group = Group.Memory, Kind = Kind.Load, Source = Name,
        });
        readings.Add(new Reading
        {
            Key = "memory.used", Label = "RAM utilisee", Value = Math.Round(usedMib, 1), Unit = "MiB",
            Group = Group.Memory, Kind = Kind.Memory, Minimum = 0.0, Maximum = Math.Round(totalMib, 1), Source = Name,
        });
        readings.Add(new Reading
        {
            Key = "memory.total", Label = "RAM totale", Value = Math.Round(totalMib, 1), Unit = "MiB",
            Group = Group.Memory, Kind = Kind.Memory, Minimum = 0.0, Maximum = Math.Round(totalMib, 1), Source = Name,
        });
        // Memoire validee (« Committed » du Gestionnaire des taches) : plus parlant sous Windows
        // qu'un pseudo-swap deduit du fichier d'echange, que l'API ne separe pas proprement.
        if (status.ullTotalPageFile > 0)
        {
            var commitPct = 100.0 * (status.ullTotalPageFile - status.ullAvailPageFile) / status.ullTotalPageFile;
            readings.Add(new Reading
            {
                Key = "memory.commit", Label = "Memoire validee", Value = Math.Round(Math.Clamp(commitPct, 0, 100), 1), Unit = "%",
                Group = Group.Memory, Kind = Kind.Load, Source = Name,
            });
        }
    }

    private void ReadIo(List<Reading> readings)
    {
        // Disque : les compteurs "/sec" sont deja des debits.
        if (_diskRead is not null && _diskWrite is not null)
        {
            readings.Add(Rate("storage.read", "Lecture disque", _diskRead.NextValue() / Mib, Group.Storage));
            readings.Add(Rate("storage.write", "Ecriture disque", _diskWrite.NextValue() / Mib, Group.Storage));
        }

        // Reseau : somme des compteurs cumulatifs de toutes les interfaces actives, differenciee.
        long inBytes = 0, outBytes = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                var stats = nic.GetIPStatistics();
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Statistiques reseau indisponibles");
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastNet is { } previous && _lastTicks > 0)
        {
            var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (elapsed > 0)
            {
                readings.Add(Rate("network.in", "Reception", Math.Max(0, inBytes - previous.In) / elapsed / Mib, Group.Network));
                readings.Add(Rate("network.out", "Emission", Math.Max(0, outBytes - previous.Out) / elapsed / Mib, Group.Network));
            }
        }
        _lastNet = (inBytes, outBytes);
        _lastTicks = now;
    }

    private Reading Rate(string key, string label, double mibPerSec, Group group) => new()
    {
        Key = key, Label = label, Value = Math.Round(mibPerSec, 2), Unit = "MiB/s",
        Group = group, Kind = Kind.Rate, Minimum = 0.0, Maximum = 100.0, Source = Name,
    };

    public override void Dispose()
    {
        _cpuTotal?.Dispose();
        foreach (var c in _cpuCores) c.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
