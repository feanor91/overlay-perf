using OverlayPerf.Config;
using OverlayPerf.Models;
using OverlayPerf.Sensors;
using OverlayPerf.Server;
using OverlayPerf.Ui;
using Xunit;

namespace OverlayPerf.Tests;

public class SnapshotTests
{
    private static Reading R(string key, double? value = 50.0, Kind kind = Kind.Load, string unit = "%", double? max = null) => new()
    {
        Key = key, Label = key, Value = value, Unit = unit, Group = Group.Cpu, Kind = kind, Maximum = max,
    };

    [Fact]
    public void FiltreParCleEtPrefixe_ConserveLOrdreDemande()
    {
        var snapshot = new Snapshot([R("cpu.load"), R("gpu.0.temp"), R("gpu.0.load"), R("memory.used")]);
        var filtered = snapshot.Filter(["gpu.*", "cpu.load"]);
        Assert.Equal(["gpu.0.temp", "gpu.0.load", "cpu.load"], filtered.Readings.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void FiltreVideRenvoieToutInchange()
    {
        var snapshot = new Snapshot([R("a"), R("b")]);
        Assert.Same(snapshot, snapshot.Filter([]));
        Assert.Same(snapshot, snapshot.Filter(null));
    }

    [Fact]
    public void FusionDedoublonneParCle_LePremierGagne()
    {
        var merged = Snapshot.Merge([[R("cpu.load", 10)], [R("cpu.load", 99), R("cpu.temp", 55)]]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(10, merged[0].Value);
    }

    [Fact]
    public void JsonCompatibleAvecLApplicationMobile()
    {
        var json = new Snapshot([R("cpu.temp", 62.5, Kind.Temperature, "°C")], timestamp: 1.5, host: "pc").ToJson();
        Assert.Equal("pc", (string?)json["host"]);
        var reading = json["readings"]![0]!;
        Assert.Equal("cpu.temp", (string?)reading["key"]);
        Assert.Equal("temperature", (string?)reading["kind"]);
        Assert.Equal("cpu", (string?)reading["group"]);
        Assert.Equal(20.0, (double?)reading["min"]);
        Assert.Equal(100.0, (double?)reading["max"]);
        Assert.True((bool?)reading["gauge"]);
    }

    [Fact]
    public void ValeurNonFinieDevientAbsente()
    {
        Assert.Null(R("x", double.NaN).Value);
        Assert.Null(R("x", double.PositiveInfinity).Value);
    }

    [Fact]
    public void FormatageDesValeursPourLOverlay()
    {
        Assert.Equal("—", OverlayForm.FormatValue(R("x", null)));
        Assert.Equal("62.5 %", OverlayForm.FormatValue(R("x", 62.5)));
        Assert.Equal("120 W", OverlayForm.FormatValue(R("x", 120.4, Kind.Power, "W")));
        Assert.Equal("1 234 RPM", OverlayForm.FormatValue(R("x", 1234, Kind.Fan, "RPM")));
        Assert.Equal("8.0 / 16.0 Go", OverlayForm.FormatValue(R("x", 8192, Kind.Memory, "MiB", 16384)));
    }
}

public class ConfigTests
{
    [Fact]
    public void ValeursParDefautSansFichier()
    {
        var config = AppConfig.Parse("");
        Assert.Equal(1.0, config.General.PollInterval);
        Assert.Equal(8777, config.Server.Port);
        Assert.Equal("auto", config.Fps.Mode);
        Assert.Contains("fps.current", config.Overlay.Metrics);
    }

    [Fact]
    public void LitLaConfigurationDeLAgentPython_EnIgnorantLesOptionsLinux()
    {
        var config = AppConfig.Parse("""
            [general]
            poll_interval = 0.5
            [sensors]
            lhm_url = "http://127.0.0.1:8085/data.json"
            disabled = ["nvidia"]
            [fps]
            mode = "mangohud"
            presentmon_path = "C:/Outils/PresentMon-2.5.1-x64.exe"
            mangohud_log_dir = ""
            [overlay]
            position = "bottom-right"
            metrics = ["fps.current", "cpu.temp"]
            """);
        Assert.Equal(0.5, config.General.PollInterval);
        Assert.Equal(["nvidia"], config.Sensors.Disabled);
        Assert.Equal("push", config.Fps.Mode);
        Assert.Equal("C:/Outils/PresentMon-2.5.1-x64.exe", config.Fps.PresentMonPath);
        Assert.Equal("bottom-right", config.Overlay.Position);
        Assert.Equal(["fps.current", "cpu.temp"], config.Overlay.Metrics);
        Assert.Contains(config.Warnings, w => w.Contains("lhm_url"));
        Assert.Contains(config.Warnings, w => w.Contains("mangohud"));
    }

    [Theory]
    [InlineData("[general]\npoll_interval = 0", "poll_interval")]
    [InlineData("[fps]\nmode = \"pigeon\"", "mode")]
    [InlineData("[server]\nport = 70000", "port")]
    [InlineData("[overlay]\nopacity = 2.0", "opacity")]
    [InlineData("[overlay]\ntext_opacity = 0.0", "text_opacity")]
    [InlineData("[overlay]\nposition = \"milieu\"", "position")]
    [InlineData("[server]\ntls_cert = \"c.pem\"", "tls_key")]
    [InlineData("[general]\nmock = \"oui\"", "booleen")]
    public void ValeursIncoherentesRefusees(string toml, string attendu)
    {
        var error = Assert.Throws<ConfigException>(() => AppConfig.Parse(toml));
        Assert.Contains(attendu, error.Message);
    }

    [Fact]
    public void OptionInconnueSignaleeSansBloquer()
    {
        var config = AppConfig.Parse("[overlay]\ncouleur = \"rouge\"\n[bidule]\nx = 1");
        Assert.Contains(config.Warnings, w => w.Contains("couleur"));
        Assert.Contains(config.Warnings, w => w.Contains("[bidule]"));
    }

    [Fact]
    public void SyntaxeTomlInvalideDevientConfigException()
    {
        Assert.Throws<ConfigException>(() => AppConfig.Parse("[general\npoll_interval = "));
    }

    [Fact]
    public void LeModeleEmbarqueEstValide()
    {
        var config = AppConfig.Parse(ConfigTemplate.Text);
        Assert.Empty(config.Warnings);
        Assert.Equal("auto", config.Fps.Mode);
        Assert.Equal(0.75, config.Overlay.Opacity);
        Assert.Equal(1.0, config.Overlay.TextOpacity);
    }

    [Fact]
    public void DeuxOpacitesIndependantes_FondPossiblementNul()
    {
        var config = AppConfig.Parse("[overlay]\nopacity = 0.0\ntext_opacity = 0.4");
        Assert.Equal(0.0, config.Overlay.Opacity);
        Assert.Equal(0.4, config.Overlay.TextOpacity);
    }
}

public class AuthTests
{
    [Fact]
    public void ExtraitLeJetonDeChaqueEmplacement()
    {
        Assert.Equal("abc", TokenAuth.Extract(h => h == "authorization" ? "Bearer abc" : null, _ => null));
        Assert.Equal("def", TokenAuth.Extract(h => h == "x-overlay-token" ? "def" : null, _ => null));
        Assert.Equal("ghi", TokenAuth.Extract(_ => null, q => q == "token" ? "ghi" : null));
        Assert.Null(TokenAuth.Extract(_ => null, _ => null));
    }

    [Fact]
    public void ComparaisonDeJeton()
    {
        Assert.True(TokenAuth.Matches("secret", "secret"));
        Assert.False(TokenAuth.Matches("secret", "Secret"));
        Assert.False(TokenAuth.Matches("secret", null));
        Assert.True(TokenAuth.Matches("", null), "jeton vide = authentification desactivee");
    }

    [Fact]
    public void XForwardedForSeulementDerriereUnProxyDeConfiance()
    {
        string? Header(string n) => n == "x-forwarded-for" ? "203.0.113.9, 10.0.0.1" : null;
        Assert.Equal("10.0.0.1", TokenAuth.ClientAddress("10.0.0.1", Header, trustProxy: false));
        Assert.Equal("203.0.113.9", TokenAuth.ClientAddress("10.0.0.1", Header, trustProxy: true));
    }

    [Fact]
    public void VerrouillageApresEchecsRepetes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var throttle = new AuthThrottle(maxFailures: 3, lockout: TimeSpan.FromSeconds(60), clock: () => now);
        throttle.RecordFailure("1.2.3.4");
        throttle.RecordFailure("1.2.3.4");
        Assert.Equal(0, throttle.RetryAfter("1.2.3.4"));
        throttle.RecordFailure("1.2.3.4");
        Assert.True(throttle.RetryAfter("1.2.3.4") > 50);
        Assert.Equal(0, throttle.RetryAfter("5.6.7.8"));
        now = now.AddSeconds(61);
        Assert.Equal(0, throttle.RetryAfter("1.2.3.4"));
    }

    [Fact]
    public void UnSuccesEffaceLArdoise()
    {
        var throttle = new AuthThrottle(maxFailures: 2);
        throttle.RecordFailure("a");
        throttle.RecordSuccess("a");
        throttle.RecordFailure("a");
        Assert.Equal(0, throttle.RetryAfter("a"));
    }
}

public class PairingTests
{
    [Fact]
    public void LeJetonPasseDansLeFragment()
    {
        Assert.Equal("http://192.168.1.10:8777/#token=a%2Fb", Pairing.PairingUrl("192.168.1.10", 8777, "a/b"));
        Assert.Equal("https://pc.exemple.fr/#token=x", Pairing.WithToken("https://pc.exemple.fr", "x"));
        Assert.Equal("http://h:1/", Pairing.PairingUrl("h", 1, ""));
    }

    [Fact]
    public void LUrlPubliquePrimeDansLeResume()
    {
        var summary = Pairing.Summary(8777, "t", publicUrl: "https://tunnel.exemple.fr");
        Assert.Equal("https://tunnel.exemple.fr/#token=t", summary.PrimaryUrl);
        Assert.StartsWith("http://", summary.LocalUrl);
    }

    [Fact]
    public void RenduTexteDuQrCode()
    {
        var qr = Pairing.RenderQrText("http://192.168.1.10:8777/#token=abc");
        Assert.Contains('█', qr);
        Assert.True(qr.Split(Environment.NewLine).Length > 10);
    }
}

public class HotkeyParsingTests
{
    [Theory]
    [InlineData("<ctrl>+<alt>+o", 0x0002u | 0x0001u, Keys.O)]
    [InlineData("<shift>+<f10>", 0x0004u, Keys.F10)]
    [InlineData("<ctrl>+<shift>+m", 0x0002u | 0x0004u, Keys.M)]
    [InlineData("<cmd>+space", 0x0008u, Keys.Space)]
    public void SyntaxePynputConvertie(string combination, uint modifiers, Keys key)
    {
        Assert.True(HotkeyManager.TryParse(combination, out var mods, out var parsed, out var error), error);
        Assert.Equal(modifiers, mods);
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<ctrl>+<alt>")]
    [InlineData("<ctrl>+o+p")]
    [InlineData("<ctrl>+<pigeon>")]
    public void CombinaisonsInvalidesRefusees(string combination)
    {
        Assert.False(HotkeyManager.TryParse(combination, out _, out _, out var error));
        Assert.NotEmpty(error);
    }
}

public class SensorTextTests
{
    [Theory]
    [InlineData("62,3 °C", 62.3)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1200RPM", 1200.0)]
    [InlineData("N/A", null)]
    [InlineData("[Not Supported]", null)]
    [InlineData("", null)]
    [InlineData("-5.5", -5.5)]
    public void ConversionDesValeursBrutes(string raw, double? expected)
    {
        Assert.Equal(expected, SensorText.ToDouble(raw));
    }

    [Fact]
    public void NomsCourtsDeGpu()
    {
        Assert.Equal("RTX 4070", SensorText.ShortGpuName("NVIDIA GeForce RTX 4070"));
        Assert.Equal("RX 7800 XT", SensorText.ShortGpuName("AMD Radeon RX 7800 XT"));
        Assert.Equal("Arc A770", SensorText.ShortGpuName("Intel(R) Arc A770"));
    }

    [Fact]
    public void SlugStable()
    {
        Assert.Equal("cpu_package", SensorText.Slug("CPU Package"));
        Assert.Equal("core_tctl_tdie", SensorText.Slug("Core (Tctl/Tdie)"));
        Assert.Equal("unknown", SensorText.Slug("  "));
    }

    [Fact]
    public void ParseNvidiaSmiCsv()
    {
        var rows = NvidiaSmiBackend.ParseCsv("0, NVIDIA GeForce RTX 4070, 55, 12, 3, 2048, 12282, 30, 45.2, 200.0, 1500, 10501\n");
        Assert.Single(rows);
        Assert.Equal("NVIDIA GeForce RTX 4070", rows[0]["name"]);
        Assert.Equal(55.0, rows[0]["temperature.gpu"]);
        Assert.Equal(12282.0, rows[0]["memory.total"]);
    }
}
