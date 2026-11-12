using ChestCounter;
using Xunit;

public class SessionsTests
{
    private static DateTime U(int h, int m = 0) => new(2026, 11, 12, h, m, 0, DateTimeKind.Utc);

    private static MatchRecord Match(DateTime startUtc, int seconds) => new() { MatchStartUtc = startUtc, DurationSeconds = seconds, AnalyzedAtUtc = startUtc.AddSeconds(seconds + 60) };

    [Fact]
    public void PcSession_IncludesMatchesSinceGameStart()
    {
        // Jeu lancé à 20:00, partie finie à 20:30, compteur démarré à 20:45 : la partie compte.
        var start = Sessions.PcSessionStart(U(20), null);
        Assert.Equal(U(20), start);
        Assert.True(Sessions.InWindow(Match(U(20, 10), 20 * 60), start!.Value));
        Assert.False(Sessions.InWindow(Match(U(19, 30), 20 * 60), start.Value));
    }

    [Fact]
    public void PcSession_GameClosed_KeepsLast() => Assert.Equal(U(18), Sessions.PcSessionStart(null, U(18)));

    [Fact]
    public void PcSession_NeverStarted_Null() => Assert.Null(Sessions.PcSessionStart(null, null));

    [Fact]
    public void InWindow_NoStartTime_UsesAnalyzedAt()
    {
        var m = new MatchRecord { AnalyzedAtUtc = U(21) };
        Assert.True(Sessions.InWindow(m, U(20)));
    }

    [Fact]
    public void TodayStart_Local()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");
        // 2026-11-12 00:30 à Bruxelles = 2026-11-11 23:30 UTC → la journée a commencé le 2026-11-11 à 23:00 UTC.
        Assert.Equal(new DateTime(2026, 11, 11, 23, 0, 0, DateTimeKind.Utc), Sessions.TodayStartUtc(new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Utc), tz));
    }
}
