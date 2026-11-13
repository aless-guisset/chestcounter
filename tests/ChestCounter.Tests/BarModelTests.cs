using ChestCounter;
using Xunit;

public class BarModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-bar-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Week2 = new(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MatchRecord M(DateTime startUtc, int chests) => new() { MatchStartUtc = startUtc, DurationSeconds = 1200, ChestsOpened = chests, AnalyzedAtUtc = startUtc.AddMinutes(25) };

    [Fact]
    public void Build_EmptyData()
    {
        var v = BarModel.ForPc(new TrackerData(), null, false, null, false, new ObtainedStore(_dir), Week2);
        Assert.Equal("0", v.Total);
        Assert.Equal(6, v.Chances.Count);
        Assert.All(v.Chances, c => Assert.Equal("0,000 %", c.Percent));
        Assert.Null(v.ChancesEmpty);
    }

    [Fact]
    public void Build_PcLive()
    {
        var d = new TrackerData();
        d.Matches.Add(M(Week2.AddHours(-1), 8));
        var live = new MatchRecord { ChestsOpened = 3 };
        var v = BarModel.ForPc(d, live, true, Week2.AddHours(-2), true, new ObtainedStore(_dir), Week2);
        Assert.Equal(BarState.Live, v.State);
        Assert.Equal("● EN DIRECT", v.Badge);
        Assert.Equal("11", v.Total);
        Assert.Equal("Partie en cours", v.Summary[0].Label);
        Assert.Equal("3", v.Summary[0].Value);
        Assert.Equal("Session", v.Summary[1].Label);
        Assert.Equal("11", v.Summary[1].Value);
    }

    [Fact]
    public void Build_PcInProgressUnreadable()
    {
        // Replay verrouillé jusqu'à la fin de la partie : pas de chiffre, donc pas « EN DIRECT ».
        var v = BarModel.ForPc(new TrackerData(), null, true, null, true, new ObtainedStore(_dir), Week2);
        Assert.Equal(BarState.Live, v.State);
        Assert.Equal("● PARTIE EN COURS", v.Badge);
        Assert.Equal("…", v.Summary.Single(l => l.Label == "Partie en cours").Value);
    }

    [Fact]
    public void Build_PcConfirmed()
    {
        var d = new TrackerData();
        d.Matches.Add(M(Week2.AddHours(-1), 8));
        var v = BarModel.ForPc(d, null, false, Week2.AddHours(-2), false, new ObtainedStore(_dir), Week2);
        Assert.Equal(BarState.Confirmed, v.State);
        Assert.Equal("✓ CONFIRMÉ (replay)", v.Badge);
        Assert.DoesNotContain(v.Summary, l => l.Label == "Partie en cours");
        Assert.Equal("Dernière partie", v.Summary[0].Label);
        Assert.Equal("8", v.Summary[0].Value);
        Assert.Equal("Dernière session", v.Summary[1].Label);
    }

    [Fact]
    public void Build_PcLastMatch_IsTheMostRecent()
    {
        var d = new TrackerData();
        d.Matches.Add(M(Week2.AddHours(-3), 4));
        d.Matches.Add(M(Week2.AddHours(-1), 9));
        d.Matches.Add(M(Week2.AddHours(-2), 6));
        var v = BarModel.ForPc(d, null, false, null, false, new ObtainedStore(_dir), Week2);
        Assert.Equal("9", v.Summary.Single(l => l.Label == "Dernière partie").Value);
    }

    [Fact]
    public void Build_PcNoMatch_NoLastMatchLine()
    {
        var v = BarModel.ForPc(new TrackerData(), null, false, null, false, new ObtainedStore(_dir), Week2);
        Assert.DoesNotContain(v.Summary, l => l.Label == "Dernière partie");
    }

    [Fact]
    public void Build_Ps5Estimate()
    {
        var v = BarModel.ForPs5(1430, true, 52, true, 0, false, 40, true, 1430, new ObtainedStore(_dir), Week2);
        Assert.StartsWith("≈ ", v.Total);
        Assert.Equal(BarState.Estimate, v.State);
        Assert.Equal("≈ ESTIMATION", v.Badge);
        Assert.DoesNotContain(v.Summary, l => l.Label == "Partie en cours");
        Assert.Equal("—", v.Summary.Single(l => l.Label == "Session").Value);
    }

    [Fact]
    public void Build_Ps5Confirmed()
    {
        var v = BarModel.ForPs5(640, false, 12, false, 12, true, 30, false, 640, new ObtainedStore(_dir), Week2);
        Assert.Equal("640", v.Total);
        Assert.Equal("✓ CONFIRMÉ (Epic)", v.Badge);
        Assert.Equal("+12", v.Summary.Single(l => l.Label == "Session").Value);
    }

    [Fact]
    public void Build_Ps5LastMatchAwaitingAnswer()
    {
        var play = new ConsolePlay(9, true, Week2.AddMinutes(-3), true, 0, true);
        var v = BarModel.ForPs5(1430, true, 52, true, 20, true, 40, true, 1430, new ObtainedStore(_dir), Week2, play);
        var last = v.Summary[0];
        Assert.Equal("Dernière partie", last.Label);
        Assert.Equal("≈ 9", last.Value);
        Assert.Contains("combien ?", last.Detail);
        Assert.Equal("≈ +20", v.Summary.Single(l => l.Label == "Session").Value);
    }

    [Fact]
    public void Build_Ps5TapsDuringMatch()
    {
        var play = new ConsolePlay(7, false, Week2.AddMinutes(-30), false, 4, false);
        var v = BarModel.ForPs5(1430, true, 52, true, 20, true, 40, true, 1430, new ObtainedStore(_dir), Week2, play);
        Assert.Equal("Partie en cours", v.Summary[0].Label);
        Assert.Equal("4", v.Summary[0].Value);
        Assert.Equal("+20", v.Summary.Single(l => l.Label == "Session").Value);
    }

    [Fact]
    public void Build_AllObtained()
    {
        var s = new ObtainedStore(_dir);
        foreach (var k in TreasureOdds.Kinds) s.Set(2, k.Kind, true);
        var v = BarModel.ForPc(new TrackerData(), null, false, null, false, s, Week2);
        Assert.Empty(v.Chances);
        Assert.Equal("Tout obtenu cette semaine", v.ChancesEmpty);
    }

    [Fact]
    public void Chances_ResetAtWeekBoundary()
    {
        var d = new TrackerData();
        d.Matches.Add(M(new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc), 100));
        var v = BarModel.ForPc(d, null, false, null, false, new ObtainedStore(_dir), Week2);
        Assert.Equal("0,000 %", v.Chances.Single(c => c.Kind == "outfit").Percent);
        Assert.Equal("0,073 %", v.Chances.Single(c => c.Kind == "pirate-shop").Percent);
    }

    [Fact]
    public void Percent_Format()
    {
        Assert.Equal("63 %", BarModel.Percent(0.6328));
        Assert.Equal("4,2 %", BarModel.Percent(0.042));
        Assert.Equal("0,092 %", BarModel.Percent(0.00092));
    }
}
