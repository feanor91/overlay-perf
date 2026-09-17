using Microsoft.Extensions.Logging;
using OverlayPerf.Config;
using Serilog;
using Serilog.Events;

namespace OverlayPerf.Logging;

/// <summary>
/// Journalisation : un fichier par jour dans <c>%LOCALAPPDATA%\overlay\logs</c>, plus la
/// console quand l'executable est lance depuis un terminal.
/// <para>Sans terminal (double-clic, demarrage automatique), le fichier est la seule trace
/// de ce qui se passe : chaque composant y ecrit ce qu'il trouve, ce qu'il lance et ce qui
/// echoue, pour qu'une mesure absente ait toujours une explication lisible.</para>
/// </summary>
public static class LogSetup
{
    public static string CurrentLogFile => Path.Combine(Paths.LogDir, $"overlay-{DateTime.Now:yyyyMMdd}.log");

    public static ILoggerFactory Create(AppConfig config, bool console)
    {
        Directory.CreateDirectory(Paths.LogDir);
        var level = config.General.LogLevel switch
        {
            "debug" or "verbose" or "trace" => LogEventLevel.Debug,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => LogEventLevel.Information,
        };

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            // Kestrel est tres bavard au niveau Information ; ses avertissements restent.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .Enrich.WithThreadId()
            .WriteTo.File(
                Path.Combine(Paths.LogDir, "overlay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, config.General.LogRetentionDays),
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId,3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (console)
        {
            serilog = serilog.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = serilog.CreateLogger();
        return LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: true));
    }

    public static void Flush() => Log.CloseAndFlush();
}
