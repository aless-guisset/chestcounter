using ChestCounter;
using Xunit;

public class ObtainedStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-obt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void MissingFile_Empty()
    {
        var s = new ObtainedStore(_dir);
        Assert.False(s.Has(1, "outfit"));
    }

    [Fact]
    public void SetHas_RoundTrip()
    {
        new ObtainedStore(_dir).Set(2, "loading", true);
        var again = new ObtainedStore(_dir);
        Assert.True(again.Has(2, "loading"));
        again.Set(2, "loading", false);
        Assert.False(new ObtainedStore(_dir).Has(2, "loading"));
    }

    [Fact]
    public void PirateShop_IsSeasonWide()
    {
        var s = new ObtainedStore(_dir);
        s.Set(1, "pirate-shop", true);
        Assert.True(s.Has(3, "pirate-shop"));
    }

    [Fact]
    public void Weekly_IsPerWeek()
    {
        var s = new ObtainedStore(_dir);
        s.Set(1, "outfit", true);
        Assert.True(s.Has(1, "outfit"));
        Assert.False(s.Has(2, "outfit"));
    }

    [Fact]
    public void AutoTick_FromPrizeId()
    {
        var s = new ObtainedStore(_dir);
        var finds = new[] { new TreasureFind { PrizeId = "DelilahPrize:dpid_item1" }, new TreasureFind { Item = "gizmo sans id" } };
        Assert.Equal(1, s.AutoTick(finds, new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc)));
        Assert.True(new ObtainedStore(_dir).Has(1, "outfit"));
        Assert.Equal(0, s.AutoTick(finds, new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc)));
    }
}
