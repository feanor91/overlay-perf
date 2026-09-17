using OverlayPerf.Fps;
using Xunit;

namespace OverlayPerf.Tests;

public class FrameTimeTrackerTests
{
    private static FrameTimeTracker Tracker(double window = 5.0, Func<double>? clock = null) =>
        new(windowSeconds: window, staleAfter: 60.0, clock: clock);

    [Fact]
    public void SansTrame_LesMesuresSontAbsentes()
    {
        var stats = new FrameTimeTracker().Stats();
        Assert.Null(stats.Fps);
        Assert.True(stats.Stale);
    }

    [Fact]
    public void MoyenneEtTempsDeTrame()
    {
        var tracker = Tracker();
        for (var i = 0; i < 120; i++) tracker.AddFrameTime(8.0);
        var stats = tracker.Stats();
        Assert.Equal(125.0, stats.Fps!.Value, 0.5);
        Assert.Equal(8.0, stats.FrameTimeMs!.Value, 0.01);
        Assert.Equal(120, stats.FrameCount);
    }

    [Fact]
    public void LesCentilesBasDecriventLesTramesLentes()
    {
        var tracker = Tracker();
        for (var i = 0; i < 980; i++) tracker.AddFrameTime(10.0);
        for (var i = 0; i < 20; i++) tracker.AddFrameTime(100.0);
        var stats = tracker.Stats();
        Assert.Equal(10.0, stats.Low1Percent!.Value, 1.0);
        Assert.True(stats.Fps > stats.Low1Percent);
    }

    [Fact]
    public void LesCentilesExigentAssezDEchantillons()
    {
        var tracker = Tracker();
        for (var i = 0; i < 10; i++) tracker.AddFrameTime(16.0);
        var stats = tracker.Stats();
        Assert.NotNull(stats.Fps);
        Assert.Null(stats.Low1Percent);
    }

    [Fact]
    public void HorodatagesSuccessifs()
    {
        var tracker = Tracker();
        for (var i = 0; i < 11; i++) tracker.AddFrame(i * 0.02);
        Assert.Equal(50.0, tracker.Stats().Fps!.Value, 0.5);
    }

    [Fact]
    public void HorodatageNonMonotoneIgnore()
    {
        var tracker = Tracker();
        tracker.AddFrame(10.0);
        tracker.AddFrame(10.0);
        tracker.AddFrame(9.0);
        Assert.Equal(0, tracker.Stats().FrameCount);
        tracker.AddFrame(9.016);
        Assert.Equal(1, tracker.Stats().FrameCount);
    }

    [Fact]
    public void CoupureDeFluxEcarteeDesCentiles()
    {
        var tracker = Tracker();
        tracker.AddFrame(10.0);
        tracker.AddFrame(13.0);
        Assert.Equal(0, tracker.Stats().FrameCount);
        tracker.AddFrameTime(5000.0);
        Assert.Equal(0, tracker.Stats().FrameCount);
    }

    [Fact]
    public void FluxInterrompuDevientPerime()
    {
        var now = 1000.0;
        var tracker = new FrameTimeTracker(windowSeconds: 1.0, staleAfter: 2.0, clock: () => now);
        tracker.AddFrameTime(16.0);
        Assert.False(tracker.Stats().Stale);
        now += 2.5;
        var stats = tracker.Stats();
        Assert.True(stats.Stale);
        Assert.Null(stats.Fps);
        Assert.Equal(1, stats.FrameCount);
    }

    [Fact]
    public void CapaciteBornee()
    {
        var tracker = new FrameTimeTracker(capacity: 50, staleAfter: 60.0);
        for (var i = 0; i < 200; i++) tracker.AddFrameTime(10.0);
        Assert.Equal(50, tracker.Stats().FrameCount);
    }

    [Fact]
    public void CentileParInterpolation()
    {
        var values = Enumerable.Range(1, 100).Select(v => (double)v).ToList();
        Assert.Equal(1.0, FrameTimeTracker.Percentile(values, 0.0));
        Assert.Equal(100.0, FrameTimeTracker.Percentile(values, 1.0));
        Assert.Equal(99.01, FrameTimeTracker.Percentile(values, 0.99), 0.001);
    }
}

public class PresentMonCsvTests
{
    private const string CsvV1 = """
        Application,ProcessID,SwapChainAddress,Runtime,Dropped,TimeInSeconds,msBetweenPresents
        jeu.exe,1234,0x1,DXGI,0,1.000,16.68
        jeu.exe,1234,0x1,DXGI,0,1.016,16.71
        jeu.exe,1234,0x1,DXGI,0,1.033,33.20
        """;

    private const string CsvV2Metrics = """
        Application,ProcessID,SwapChainAddress,PresentRuntime,FrameType,CPUStartTime,FrameTime
        jeu.exe,99,0x2,DXGI,Application,1.000,8.33
        jeu.exe,99,0x2,DXGI,Application,1.008,8.31
        jeu.exe,99,0x2,DXGI,Application,1.016,N/A
        """;

    /// <summary>En-tete reel de PresentMon 2.3+ par defaut : « MsBetweenPresents », M majuscule.</summary>
    private const string CsvV2Default = """
        Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInSeconds,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed
        jeu.exe,1234,0x1,DXGI,1,0,0,Hardware: Independent Flip,1.000,8.30,8.33,8.33,0.10,5.00,12.00
        jeu.exe,1234,0x1,DXGI,1,0,0,Hardware: Independent Flip,1.008,8.30,8.31,8.31,0.10,5.00,12.00
        """;

    [Theory]
    [InlineData(CsvV1, 3, 22.2)]
    [InlineData(CsvV2Metrics, 2, 8.32)]
    [InlineData(CsvV2Default, 2, 8.32)]
    public void LectureDesFormatsPresentMon(string csv, int frames, double meanMs)
    {
        var tracker = new FrameTimeTracker(windowSeconds: 5.0, staleAfter: 60.0);
        var parser = new PresentMonCsv(tracker);
        Assert.Equal(frames, parser.Consume(new StringReader(csv)));
        var stats = tracker.Stats();
        Assert.Equal(frames, stats.FrameCount);
        Assert.Equal(meanMs, stats.FrameTimeMs!.Value, 0.05);
        Assert.Equal("jeu.exe", stats.Application);
    }

    [Fact]
    public void CsvSansColonneDeDureeNeProduitRienEtLeSignale()
    {
        string? signaled = null;
        var parser = new PresentMonCsv(new FrameTimeTracker(), header => signaled = header);
        Assert.Equal(0, parser.Consume(new StringReader("Application,ProcessID\njeu.exe,1\n")));
        Assert.Equal("Application,ProcessID", signaled);
    }

    [Fact]
    public void EnteteRejoueEnCoursDeFlux()
    {
        var tracker = new FrameTimeTracker(windowSeconds: 5.0, staleAfter: 60.0);
        var parser = new PresentMonCsv(tracker);
        parser.Consume(new StringReader(CsvV2Metrics + "\n" + CsvV1));
        Assert.Equal(5, tracker.Stats().FrameCount);
    }

    [Fact]
    public void ArretDemandeInterromptLaLecture()
    {
        var tracker = new FrameTimeTracker();
        new PresentMonCsv(tracker).Consume(new StringReader(CsvV1), shouldStop: () => true);
        Assert.Equal(0, tracker.Stats().FrameCount);
    }

    [Fact]
    public void LesExclusionsParDefautNeContiennentPasDeJeu()
    {
        Assert.Contains("explorer.exe", PresentMonSource.DefaultExcludes);
        Assert.Contains("OverlayPerf.exe", PresentMonSource.DefaultExcludes);
        // Le Gestionnaire des taches (Windows 11, rendu accelere) et l'Explorateur presentent
        // en continu sans etre des jeux : rencontre en pratique comme cause de FPS errones a
        // l'ecran de bureau (144 FPS affiches alors qu'aucun jeu n'est lance).
        Assert.Contains("Taskmgr.exe", PresentMonSource.DefaultExcludes);
        Assert.All(PresentMonSource.DefaultExcludes, n => Assert.EndsWith(".exe", n, StringComparison.OrdinalIgnoreCase));
    }
}

public class PresentMonTargetingTests
{
    // PresentMonSource.LegitimateCandidate est la decision au coeur du ciblage dynamique par
    // application au premier plan : elle remplace une liste noire forcement incomplete (une
    // application non prevue, comme le Gestionnaire des taches, peut toujours s'y glisser).
    private static readonly IReadOnlyList<string> Excludes = ["explorer.exe", "Taskmgr.exe"];

    [Fact]
    public void AucuneFenetreAuPremierPlanNeChangeRienALaCible()
    {
        Assert.Null(PresentMonSource.LegitimateCandidate(null, Excludes));
    }

    [Fact]
    public void UneApplicationDeLaListeNoireNestJamaisUneCibleLegitime()
    {
        Assert.Null(PresentMonSource.LegitimateCandidate("Taskmgr.exe", Excludes));
        Assert.Null(PresentMonSource.LegitimateCandidate("EXPLORER.EXE", Excludes)); // casse ignoree
    }

    [Fact]
    public void UneApplicationInconnueDevientLaCible()
    {
        Assert.Equal("cyberpunk2077.exe", PresentMonSource.LegitimateCandidate("cyberpunk2077.exe", Excludes));
    }
}
