using System.Text.Json.Nodes;
using ChestCounter;
using Xunit;

public class ConsoleMatchesTests
{
    private static readonly DateTime T = new(2026, 11, 13, 10, 46, 0, DateTimeKind.Utc);

    private static JsonNode Profile(int level, int xp) =>
        JsonNode.Parse($"{{\"rvn\":1,\"stats\":{{\"attributes\":{{\"level\":{level},\"xp\":{xp},\"book_xp\":{xp}}}}}}}")!;

    [Fact]
    public void XpKey_ReadsLevelAndXp()
    {
        Assert.Equal("118:25699", ConsoleMatches.XpKey(Profile(118, 25699)));
        Assert.Null(ConsoleMatches.XpKey(JsonNode.Parse("{\"stats\":{}}")!));
    }

    [Fact]
    public void Ended_OnlyWhenKeyChanges()
    {
        Assert.False(ConsoleMatches.Ended(null, "118:25699")); // premier relevé : pas une fin de partie
        Assert.False(ConsoleMatches.Ended("118:25699", "118:25699"));
        Assert.True(ConsoleMatches.Ended("118:25699", "118:30276"));
        Assert.True(ConsoleMatches.Ended("118:80000", "119:1200")); // passage de niveau
        Assert.False(ConsoleMatches.Ended("118:25699", null));
    }

    [Fact]
    public void Value_AnswerThenTapsThenAverage()
    {
        Assert.Equal((7, false), ConsoleMatches.Value(new PlayedMatch { Chests = 7, Taps = 3 }, 9.4));
        Assert.Equal((3, false), ConsoleMatches.Value(new PlayedMatch { Taps = 3 }, 9.4));
        Assert.Equal((9, true), ConsoleMatches.Value(new PlayedMatch(), 9.4));
        Assert.Equal((0, true), ConsoleMatches.Value(new PlayedMatch(), null));
        Assert.Equal((0, false), ConsoleMatches.Value(new PlayedMatch { Chests = 0 }, 9.4)); // « 0 coffre » est une vraie réponse
    }

    [Fact]
    public void Sum_FlagsEstimates()
    {
        var list = new[] { new PlayedMatch { Chests = 5 }, new PlayedMatch { Taps = 2 } };
        Assert.Equal((7, false), ConsoleMatches.Sum(list, 8));
        Assert.Equal((15, true), ConsoleMatches.Sum(list.Append(new PlayedMatch()), 8));
        Assert.Equal((0, false), ConsoleMatches.Sum(Array.Empty<PlayedMatch>(), 8));
    }

    [Fact]
    public void Average_PrefersOwnAnswersOnceThereAreThree()
    {
        var two = new[] { new PlayedMatch { Chests = 2 }, new PlayedMatch { Chests = 4 } };
        Assert.Equal(10, ConsoleMatches.Average(two, 10));
        Assert.Equal(3, ConsoleMatches.Average(two, null));
        var three = two.Append(new PlayedMatch { Taps = 6 }).Append(new PlayedMatch()).ToList();
        Assert.Equal(4, ConsoleMatches.Average(three, 10)); // (2 + 4 + 6) / 3, la partie sans réponse ne compte pas
        Assert.Null(ConsoleMatches.Average(Array.Empty<PlayedMatch>(), null));
    }

    [Fact]
    public void TotalCorrection_ReplacesPaceForPublishedDays_AddsUnpublished()
    {
        var published = new PlayedMatch { EndedUtc = T.AddDays(-1), Chests = 12 };     // Epic l'a déjà comptée à 9 (rythme)
        var publishedNoAnswer = new PlayedMatch { EndedUtc = T.AddDays(-1) };          // déjà comptée au rythme : rien à changer
        var today = new PlayedMatch { EndedUtc = T, Taps = 4 };                         // pas encore publiée par Epic : on l'ajoute
        var todayNoAnswer = new PlayedMatch { EndedUtc = T };                          // pas publiée, sans réponse : la moyenne
        bool Published(DateTime utc) => utc.Date < T.Date;
        var c = ConsoleMatches.TotalCorrection(new[] { published, publishedNoAnswer, today, todayNoAnswer }, ratio: 9, average: 8, Published);
        Assert.Equal((12 - 9) + 0 + 4 + 8, c);
    }

    [Fact]
    public void AddEnded_KeepsTapsAndEpicDelta()
    {
        var list = new List<PlayedMatch>();
        var m = ConsoleMatches.AddEnded(list, T, taps: 3, epicDelta: null);
        Assert.Single(list);
        Assert.Equal(3, m.Taps);
        Assert.Null(m.Chests);
        Assert.False(string.IsNullOrEmpty(m.Id));
        var exact = ConsoleMatches.AddEnded(list, T.AddMinutes(20), taps: 0, epicDelta: 6);
        Assert.Equal(6, exact.Chests); // sous le plafond : Epic donne le chiffre exact
        Assert.Equal("epic", exact.Source);
    }

    [Fact]
    public void AddEnded_TrimsOldMatches()
    {
        var list = Enumerable.Range(0, ConsoleMatches.Keep).Select(i => new PlayedMatch { Id = i.ToString(), EndedUtc = T.AddHours(-i) }).ToList();
        ConsoleMatches.AddEnded(list, T.AddHours(1), 0, null);
        Assert.Equal(ConsoleMatches.Keep, list.Count);
    }

    [Fact]
    public void Value_ScreenReadingBeatsTapsAndAverage()
    {
        Assert.Equal((8, false), ConsoleMatches.Value(new PlayedMatch { Seen = 8, Taps = 3 }, 9.4));
        Assert.Equal((0, false), ConsoleMatches.Value(new PlayedMatch { Seen = 0 }, 9.4));   // lu à l'écran : vraiment 0
        Assert.Equal((5, false), ConsoleMatches.Value(new PlayedMatch { Seen = 8, Chests = 5 }, 9.4)); // la réponse du joueur gagne
    }

    [Fact]
    public void AddEnded_KeepsScreenCounts()
    {
        var list = new List<PlayedMatch>();
        var m = ConsoleMatches.AddEnded(list, T, taps: 0, epicDelta: null, seen: 8, ammo: 2);
        Assert.Equal(8, m.Seen);
        Assert.Equal(2, m.Ammo);
        Assert.Equal("screen", m.Source);
        var none = ConsoleMatches.AddEnded(list, T, taps: 0, epicDelta: null);
        Assert.Null(none.Seen);
    }
}
