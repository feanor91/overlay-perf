using Microsoft.Extensions.Logging;
using OverlayPerf.Config;

namespace OverlayPerf.Sensors;

public static class SensorDetection
{
    /// <summary>
    /// Construit la liste des backends exploitables sur la machine courante.
    /// L'ordre porte la priorite : le premier a publier une cle gagne (le systeme pour
    /// <c>cpu.load</c> et la memoire, LibreHardwareMonitor pour le reste, nvidia-smi en secours).
    /// <paramref name="warnings"/> recoit un message actionnable pour chaque source attendue mais absente.
    /// </summary>
    public static List<SensorBackend> Detect(AppConfig config, ILoggerFactory logs, List<string> warnings)
    {
        var log = logs.CreateLogger("overlay.sensors");
        var disabled = new HashSet<string>(config.Sensors.Disabled);
        var backends = new List<SensorBackend>();

        if (config.General.Mock)
        {
            log.LogInformation("Mode simulation : aucun capteur reel interroge");
            backends.Add(new MockBackend(logs.CreateLogger("overlay.sensors.mock")));
            return backends;
        }

        if (!disabled.Contains("system"))
        {
            backends.Add(new SystemBackend(logs.CreateLogger("overlay.sensors.system"),
                config.Sensors.PerCore, config.Sensors.IncludeIo));
        }

        var lhmOk = false;
        var nvidiaCovered = false;
        if (!disabled.Contains("lhm"))
        {
            var lhm = new LibreHardwareBackend(logs.CreateLogger("overlay.sensors.lhm"), config.Sensors.PerCore);
            if (lhm.Open())
            {
                backends.Add(lhm);
                lhmOk = true;
                nvidiaCovered = lhm.NvidiaGpuCount > 0;
            }
            else
            {
                lhm.Dispose();
                warnings.Add("LibreHardwareMonitor n'a pas pu demarrer : temperatures, ventilateurs et "
                             + "consommations resteront absents. Details dans le journal.");
            }
        }

        if (!disabled.Contains("nvidia") && !nvidiaCovered)
        {
            // Secours seulement : quand LibreHardwareMonitor voit deja la carte NVIDIA, nvidia-smi
            // publierait des cles gpu.N.* concurrentes avec une numerotation differente.
            var nvidia = new NvidiaSmiBackend(logs.CreateLogger("overlay.sensors.nvidia"));
            if (nvidia.Available)
            {
                backends.Add(nvidia);
                log.LogInformation("nvidia-smi trouve{Role}", lhmOk ? " (LibreHardwareMonitor ne voit aucune carte NVIDIA)" : "");
            }
            else
            {
                nvidia.Dispose();
            }
        }

        if (backends.Count == 0)
        {
            warnings.Add("Aucun capteur detecte : bascule sur le backend de demonstration.");
            backends.Add(new MockBackend(logs.CreateLogger("overlay.sensors.mock")));
        }

        log.LogInformation("Backends actifs : {Backends}", string.Join(", ", backends.Select(b => b.Name)));
        return backends;
    }
}
