namespace ChestCounter;

/// <summary>
/// Session et journée de la barre. PC : la session commence au lancement de Fortnite (même si le compteur est ouvert
/// après) et, jeu fermé, la dernière session reste affichée. Console : du démarrage de ChestCounter à son arrêt
/// (géré dans Ps5Mode). « Aujourd'hui » commence à minuit, heure locale.
/// </summary>
public static class Sessions
{
    /// <summary>Jeu lancé → son heure de lancement ; fermé → la dernière connue (null si jamais vu).</summary>
    public static DateTime? PcSessionStart(DateTime? gameStartUtc, DateTime? lastKnownStartUtc) => gameStartUtc ?? lastKnownStartUtc;

    /// <summary>Fin de la partie (début + durée, sinon heure d'analyse) au moins égale au début de la fenêtre.</summary>
    public static bool InWindow(MatchRecord m, DateTime startUtc) => EndUtc(m) >= startUtc;

    public static DateTime EndUtc(MatchRecord m)
    {
        if (m.MatchStartUtc is { } s)
        {
            return AsUtc(s).AddSeconds(m.DurationSeconds ?? 0);
        }
        return AsUtc(m.AnalyzedAtUtc);
    }

    /// <summary>Minuit local du jour en cours, en UTC.</summary>
    public static DateTime TodayStartUtc(DateTime utcNow, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), tz);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), tz);
    }

    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };
}
