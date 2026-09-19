using OverlayPerf.Config;
using OverlayPerf.Models;
using OverlayPerf.Ui;
using Xunit;

namespace OverlayPerf.Tests;

public class TomlPatcherTests
{
    private const string Fichier = """
        # Configuration d'Overlay.

        [general]
        # Periode d'echantillonnage.
        poll_interval = 1.0

        [overlay]
        enabled = true

        # top-left, top-right, bottom-left, bottom-right
        position = "top-left"
        opacity = 0.85   # commentaire en fin de ligne

        # Mesures affichees, dans l'ordre.
        metrics = [
            "fps.current",
            "fan.*",
        ]

        [server]
        port = 8777
        """;

    [Fact]
    public void RemplaceLesScalairesEnGardantLesCommentaires()
    {
        var result = TomlPatcher.SetValues(Fichier, "overlay", new Dictionary<string, string>
        {
            ["position"] = TomlPatcher.String("bottom-right"),
            ["opacity"] = TomlPatcher.Double(0.6),
        });
        Assert.Contains("# top-left, top-right, bottom-left, bottom-right", result);
        Assert.Contains("position = \"bottom-right\"", result);
        Assert.Contains("opacity = 0.6", result);
        Assert.DoesNotContain("opacity = 0.85", result);
        Assert.Contains("# Periode d'echantillonnage.", result);
        Assert.Contains("port = 8777", result);
    }

    [Fact]
    public void RemplaceUnTableauMultiligne()
    {
        var result = TomlPatcher.SetValues(Fichier, "overlay", new Dictionary<string, string>
        {
            ["metrics"] = TomlPatcher.StringArray(["cpu.load", "gpu.0.temp"]),
        });
        Assert.DoesNotContain("\"fan.*\"", result);
        Assert.Contains("\"cpu.load\"", result);
        Assert.Contains("# Mesures affichees, dans l'ordre.", result);
        // Le fichier reste lisible et la section suivante intacte.
        var config = AppConfig.Parse(result);
        Assert.Equal(["cpu.load", "gpu.0.temp"], config.Overlay.Metrics);
        Assert.Equal(8777, config.Server.Port);
    }

    [Fact]
    public void AjouteLesClesAbsentesDansLaSection()
    {
        var result = TomlPatcher.SetValues(Fichier, "overlay", new Dictionary<string, string>
        {
            ["columns"] = TomlPatcher.Int(2),
            ["click_through"] = TomlPatcher.Bool(false),
        });
        var config = AppConfig.Parse(result);
        Assert.Equal(2, config.Overlay.Columns);
        Assert.False(config.Overlay.ClickThrough);
        // Inseree dans [overlay], pas dans [server].
        var overlayIndex = result.IndexOf("[overlay]", StringComparison.Ordinal);
        var serverIndex = result.IndexOf("[server]", StringComparison.Ordinal);
        var columnsIndex = result.IndexOf("columns = 2", StringComparison.Ordinal);
        Assert.InRange(columnsIndex, overlayIndex, serverIndex);
    }

    [Fact]
    public void CreeLaSectionSiAbsente()
    {
        var result = TomlPatcher.SetValues("[general]\npoll_interval = 2.0\n", "overlay", new Dictionary<string, string>
        {
            ["position"] = TomlPatcher.String("top-right"),
        });
        var config = AppConfig.Parse(result);
        Assert.Equal("top-right", config.Overlay.Position);
        Assert.Equal(2.0, config.General.PollInterval);
    }

    [Fact]
    public void ConserveLesFinsDeLigneWindows()
    {
        var crlf = Fichier.Replace("\n", "\r\n");
        var result = TomlPatcher.SetValues(crlf, "overlay", new Dictionary<string, string> { ["margin"] = "12" });
        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\n\n\n", result.Replace("\r\n", "\n"));
    }

    [Fact]
    public void LitterauxToml()
    {
        Assert.Equal("\"a \\\"b\\\" \\\\ c\"", TomlPatcher.String("a \"b\" \\ c"));
        Assert.Equal("1.0", TomlPatcher.Double(1));
        Assert.Equal("0.85", TomlPatcher.Double(0.85));
        Assert.Equal("[]", TomlPatcher.StringArray([]));
        Assert.Equal("true", TomlPatcher.Bool(true));
    }
}

public class ConfigWriterTests
{
    [Fact]
    public void EnregistreLesReglagesDeLOverlayDansUnFichierExistant()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overlayperf-test-{Guid.NewGuid():N}.toml");
        try
        {
            File.WriteAllText(path, ConfigTemplate.Text);
            var overlay = new OverlayConfig
            {
                Position = "bottom-left", Opacity = 0.5, TextOpacity = 0.8, Margin = 10, FontSize = 18, Columns = 2,
                ClickThrough = false, Hotkey = "<shift>+<f10>", HotkeyQuit = "<shift>+<f11>", VisibleAtStart = false,
                Metrics = ["gpu.0.fan.rpm", "cpu.temp"],
            };
            ConfigWriter.SaveOverlay(path, overlay);

            var reloaded = AppConfig.Load(path);
            Assert.Equal("bottom-left", reloaded.Overlay.Position);
            Assert.Equal(0.5, reloaded.Overlay.Opacity);
            Assert.Equal(0.8, reloaded.Overlay.TextOpacity);
            Assert.Equal(18, reloaded.Overlay.FontSize);
            Assert.Equal("<shift>+<f10>", reloaded.Overlay.Hotkey);
            Assert.False(reloaded.Overlay.VisibleAtStart);
            Assert.Equal(["gpu.0.fan.rpm", "cpu.temp"], reloaded.Overlay.Metrics);
            Assert.Empty(reloaded.Warnings);
            // Les commentaires du modele sont toujours la.
            Assert.Contains("# Laisser passer les clics vers le jeu situe dessous.", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnregistreLesOptionsPresentMon()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overlayperf-test-{Guid.NewGuid():N}.toml");
        try
        {
            File.WriteAllText(path, ConfigTemplate.Text);
            ConfigWriter.SaveFps(path, new FpsConfig { PresentMonPath = "D:/Outils/PresentMon.exe", TrackFrameGeneration = true });
            var reloaded = AppConfig.Load(path);
            Assert.Equal("D:/Outils/PresentMon.exe", reloaded.Fps.PresentMonPath);
            Assert.True(reloaded.Fps.TrackFrameGeneration);
            Assert.Empty(reloaded.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreeLeFichierDepuisLeModeleSiAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overlayperf-test-{Guid.NewGuid():N}", "config.toml");
        try
        {
            ConfigWriter.SaveOverlay(path, new OverlayConfig { Position = "top-right" });
            Assert.Equal("top-right", AppConfig.Load(path).Overlay.Position);
            Assert.Contains("[server]", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class SettingsDialogTests
{
    [Fact]
    public void LaFenetreSeConstruitAvecUnInstantane()
    {
        var snapshot = new Snapshot(
        [
            new Reading { Key = "fps.current", Label = "FPS", Value = 60, Unit = "FPS", Group = Group.Fps, Kind = Kind.Fps },
            new Reading { Key = "fan.mb.fan_1", Label = "Fan #1", Value = 0, Unit = "RPM", Group = Group.Fan, Kind = Kind.Fan },
            new Reading { Key = "gpu.0.fan.rpm", Label = "RTX ventilateur (RPM)", Value = null, Unit = "RPM", Group = Group.Gpu, Kind = Kind.Fan },
        ]);
        var config = new OverlayConfig { Metrics = ["fps.current", "fan.*"] };
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new SettingsDialog(config, new FpsConfig(), () => snapshot, () => { }, () => { });
                dialog.CreateControl();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
