using ChestCounter;
using Xunit;

public class TreasureOddsTests
{
    private static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void Chance_ZeroChests() => Assert.Equal(0, TreasureOdds.Chance(272, 0));

    [Fact]
    public void Chance_Known() => Assert.InRange(TreasureOdds.Chance(272, 272), 0.6323, 0.6333);

    [Fact]
    public void CurrentWeek_Boundary()
    {
        Assert.Equal(1, TreasureOdds.CurrentWeek(Utc("2026-11-08T13:59:59Z")).Number);
        Assert.Equal(2, TreasureOdds.CurrentWeek(Utc("2026-11-08T14:00:00Z")).Number);
        Assert.Equal(Utc("2026-11-08T14:00:00Z"), TreasureOdds.CurrentWeek(Utc("2026-11-10T00:00:00Z")).StartUtc);
    }

    [Fact]
    public void CurrentWeek_Unknown()
    {
        var w = TreasureOdds.CurrentWeek(Utc("2026-12-20T12:00:00Z"));
        Assert.Equal(7, w.Number);
        Assert.Empty(w.Prizes);
        Assert.Equal(Utc("2026-12-13T14:00:00Z"), w.StartUtc);
        Assert.Equal(Utc("2026-12-20T14:00:00Z"), w.EndUtc);
    }

    [Fact]
    public void FindByPrizeId_Known() => Assert.Equal("w1-outfit", TreasureOdds.FindByPrizeId("DelilahPrize:dpid_item1")?.Id);

    [Fact]
    public void FindByPrizeId_PirateShop() => Assert.Equal("pirate-shop", TreasureOdds.FindByPrizeId("9f73f94d-2e61-4523-97d7-d61235d3365e")?.Kind);

    [Fact]
    public void Kinds_SixWithoutGizmo()
    {
        Assert.Equal(6, TreasureOdds.Kinds.Count);
        Assert.DoesNotContain(TreasureOdds.Kinds, k => k.Kind == "gizmo");
        Assert.Equal(1562500, TreasureOdds.Kinds.Single(k => k.Kind == "outfit").OneIn);
        Assert.False(TreasureOdds.Kinds.Single(k => k.Kind == "pirate-shop").Weekly);
    }
}
