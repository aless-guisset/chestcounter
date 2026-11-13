using ChestCounter;
using Xunit;

public class XpPopupTests
{
    private static ScreenOcr.Line L(string t, double x, double y, double w = 15, double h = 6) => new(t, x, y, w, h);

    [Fact]
    public void Parse_XpWithLabelBelow()
    {
        var ev = XpPopup.Parse(new[] { L("+900 XP", 40, 30), L("RARE CHEST", 42, 37) });
        var e = Assert.Single(ev);
        Assert.Equal(900, e.Xp);
        Assert.Equal("RARE CHEST", e.Label);
    }

    [Fact]
    public void Parse_OcrNoise()
    {
        // L'OCR lit « + 300 Xp », « '9,724 XP », espaces et casse variables.
        Assert.Equal(300, Assert.Single(XpPopup.Parse(new[] { L("+ 300 Xp", 40, 30), L("SLURP BARREL", 41, 37) })).Xp);
        Assert.Equal(1250, Assert.Single(XpPopup.Parse(new[] { L("+1,250 XP", 40, 30), L("ELIMINATION", 41, 37) })).Xp);
    }

    [Fact]
    public void Parse_XpWithoutLabel_StillAnEvent()
    {
        var e = Assert.Single(XpPopup.Parse(new[] { L("+700 XP", 40, 30) }));
        Assert.Equal(700, e.Xp);
        Assert.Null(e.Label);
    }

    [Fact]
    public void Parse_IgnoresTextFarAwayOrAbove()
    {
        var e = Assert.Single(XpPopup.Parse(new[] { L("CHEST", 41, 20), L("+600 XP", 40, 30), L("NO AMMO", 80, 80) }));
        Assert.Null(e.Label);
    }

    [Fact]
    public void Parse_IgnoresLevelProgressText()
    {
        Assert.Empty(XpPopup.Parse(new[] { L("65,120 XP", 40, 30), L("5,120 XP to 120", 40, 40) })); // pas de « + »
    }

    [Theory]
    [InlineData("CHEST", "chest")]
    [InlineData("RARE CHEST", "rare-chest")]
    [InlineData("Coffre", "chest")]
    [InlineData("COFFRE RARE", "rare-chest")]
    [InlineData("AMMO BOX", "ammo")]
    [InlineData("RARE AMMO BOX", "ammo")]
    [InlineData("BOÎTE DE MUNITIONS", "ammo")]
    [InlineData("ELIMINATION", "elimination")]
    [InlineData("RARE CREST", "rare-chest")]
    [InlineData("RARE AMMO gox", "ammo")]
    [InlineData("CHES", "chest")]
    [InlineData("SLURP BARREL", "other")]
    [InlineData(null, "unknown")]
    public void Kind_FromLabel(string? label, string kind) => Assert.Equal(kind, XpPopup.Kind(label));

    [Fact]
    public void Tracker_SamePopupOnSeveralFramesIsOneEvent()
    {
        var tr = new XpPopup.Tracker();
        var t = new DateTime(2026, 11, 13, 14, 4, 0);
        tr.Add(new XpPopup.Event(900, null), t);                 // 1re image : étiquette pas encore lue
        tr.Add(new XpPopup.Event(900, "RARE CREST"), t.AddSeconds(0.5));
        tr.Add(new XpPopup.Event(900, "RARE"), t.AddSeconds(1));
        Assert.Empty(tr.Flush(t.AddSeconds(1.5)));               // encore affiché : pas fini
        var e = Assert.Single(tr.Flush(t.AddSeconds(3)));
        Assert.Equal(900, e.Xp);
        Assert.Equal("RARE CREST", e.Label);                     // la meilleure étiquette (celle qui dit le type)
    }

    [Fact]
    public void Tracker_LabelOnlyJoinsTheXp()
    {
        var tr = new XpPopup.Tracker();
        var t = new DateTime(2026, 11, 13, 14, 4, 0);
        tr.Add(new XpPopup.Event(600, null), t);
        tr.Add(new XpPopup.Event(0, "RARE AMMO gox"), t.AddSeconds(0.5)); // l'étiquette lue sans le « +600 XP »
        var e = Assert.Single(tr.Flush(t.AddSeconds(5)));
        Assert.Equal((600, "ammo"), (e.Xp, XpPopup.Kind(e.Label)));
    }

    [Fact]
    public void Tracker_LabelFirstThenXpWithPartialLabel()
    {
        var tr = new XpPopup.Tracker();
        var t = new DateTime(2026, 11, 13, 14, 4, 0);
        tr.Add(new XpPopup.Event(0, "RARE CREST"), t);
        tr.Add(new XpPopup.Event(900, "RARE"), t);
        var e = Assert.Single(tr.Flush(t.AddSeconds(5)));
        Assert.Equal((900, "rare-chest"), (e.Xp, XpPopup.Kind(e.Label)));
    }

    [Fact]
    public void Tracker_TwoChestsInARow()
    {
        var tr = new XpPopup.Tracker();
        var t = new DateTime(2026, 11, 13, 14, 4, 0);
        tr.Add(new XpPopup.Event(600, "CHEST"), t);
        tr.Add(new XpPopup.Event(600, "CHEST"), t.AddSeconds(6));
        Assert.Equal(2, tr.Flush(t.AddSeconds(10)).Count);
    }

    [Fact]
    public void GameArea_CropsBlackBars()
    {
        // Fenêtre 100×100 : image du jeu de y=25 à y=74 (bandes noires en haut et en bas, comme Chiaki).
        var px = new byte[100 * 100 * 4];
        for (var y = 25; y < 75; y++)
            for (var x = 0; x < 100; x++)
                px[(y * 100 + x) * 4 + 1] = 120;
        for (var x = 0; x < 100; x++) px[(2 * 100 + x) * 4] = 40; // barre de titre gris foncé
        for (var x = 5; x < 15; x++) px[(3 * 100 + x) * 4] = 230; // un peu de texte dans la barre de titre
        var (x0, y0, w, h) = XpPopup.GameArea(new ScreenOcr.Frame(px, 100, 100));
        Assert.Equal((0, 25, 100, 50), (x0, y0, w, h));
    }

    [Fact]
    public void Zone_BlackOrTinyFrame_DoesNotThrow()
    {
        // Écran de chargement tout noir : pas d'image du jeu trouvée, on lit quand même sans planter.
        var black = new ScreenOcr.Frame(new byte[640 * 360 * 4], 640, 360);
        var z = XpPopup.Zone(black, 3, null);
        Assert.True(z.Width > 0 && z.Height > 0);
        var lit = new byte[640 * 360 * 4];
        for (var x = 0; x < 640; x++) lit[(359 * 640 + x) * 4 + 1] = 200; // seule la dernière ligne est claire
        XpPopup.Zone(new ScreenOcr.Frame(lit, 640, 360), 4, null);
    }

    [Theory]
    [InlineData(new[] { "PLAY", "SHOP", "LOCKER", "PASSES", "QUESTS" }, true)]
    [InlineData(new[] { "JOUER", "BOUTIQUE", "CASIER", "QUÊTES" }, true)]
    [InlineData(new[] { "LOCKER" }, false)]                // un seul mot : pas assez sûr
    [InlineData(new[] { "+600 XP", "CHEST", "0:27" }, false)]
    public void IsLobby_FromTopMenu(string[] words, bool lobby) =>
        Assert.Equal(lobby, XpPopup.IsLobby(words.Select(w => new ScreenOcr.Line(w, 10, 5, 5, 3))));

    [Theory]
    [InlineData(new[] { "Next Map 3m", "Squid Grounds", "RELOAD", "Zero Build, Solo", "PLAY" }, "reload")]
    [InlineData(new[] { "RELØAD", "Zero Build, Solo" }, "reload")]
    [InlineData(new[] { "REI.OAD", "-2; Zero Build, Solo" }, "reload")]   // lu tel quel sur ton lobby
    [InlineData(new[] { "BATTLE ROYALE", "Zero Build, Duos" }, "br")]
    [InlineData(new[] { "BATTLE", "ROYALE", "Solo" }, "br")]
    [InlineData(new[] { "RANKED", "Battle Royale" }, "br")]
    [InlineData(new[] { "LEGO Fortnite", "Odyssey" }, "other")]
    [InlineData(new[] { "FESTIVAL" }, "other")]
    [InlineData(new[] { "PLAY" }, null)]                     // rien de lisible : on ne sait pas
    public void ModeOf_LobbyCard(string[] lines, string? mode) =>
        Assert.Equal(mode, XpPopup.ModeOf(lines.Select(t => new ScreenOcr.Line(t, 10, 50, 10, 3))));

    [Theory]
    [InlineData("br", "br", true)]
    [InlineData("br", "reload", false)]
    [InlineData("br", "other", false)]
    [InlineData("br", null, true)]             // mode pas lu : on compte (mieux vaut trop que rien), le journal le dit
    [InlineData("br+reload", "reload", true)]
    [InlineData("br+reload", "other", false)]
    [InlineData("all", "other", true)]
    [InlineData(null, "reload", false)]         // réglage absent = Battle Royale seulement
    public void ModeAllowed(string? setting, string? mode, bool allowed) => Assert.Equal(allowed, LiveScreen.Allowed(setting, mode));
}
