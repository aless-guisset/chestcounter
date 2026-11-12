namespace ChestCounter;

/// <summary>Live = partie PC en cours ; Confirmed = replays terminés ; Estimate = chiffre console estimé ; EstimateConfirmed = chiffres Epic sûrs.</summary>
public enum BarState { Live, Confirmed, Estimate, EstimateConfirmed }

/// <summary>Une ligne de l'onglet Résumé (« Session », « 31 », « 3 parties · moy. 10 »).</summary>
public sealed record BarLine(string Label, string Value, string? Detail = null);

/// <summary>Une ligne de l'onglet Coffres : trésor, chance en %, remplissage de la jauge (0..1), cote.</summary>
public sealed record ChanceLine(string Kind, string Label, string Percent, double Fill, string Odds);

/// <summary>Ce que la barre affiche. ChancesEmpty : texte à la place de la liste quand tout est obtenu.</summary>
public sealed record BarView(string Total, BarState State, string Badge, IReadOnlyList<BarLine> Summary, IReadOnlyList<ChanceLine> Chances, string? ChancesEmpty);

/// <summary>Construit la barre des coffres depuis les données PC (replays) ou console (compte Epic).</summary>
public static class BarModel
{
    public static BarView ForPc(TrackerData d, MatchRecord? live, bool inProgress, DateTime? sessionStartUtc, bool sessionRunning, ObtainedStore obtained, DateTime utcNow)
    {
        var liveChests = inProgress ? live?.ChestsOpened ?? 0 : 0;
        var total = d.Matches.Sum(m => m.ChestsOpened) + liveChests;
        var lines = new List<BarLine>();
        if (inProgress)
        {
            lines.Add(new BarLine(L.T("Partie en cours", "Current match", "Partida actual"), live != null ? L.N(live.ChestsOpened) : "…"));
        }
        else if (d.Matches.Count > 0)
        {
            // Entre deux parties : les coffres de la partie qui vient de finir, avec son heure de fin.
            var last = d.Matches.MaxBy(Sessions.EndUtc)!;
            lines.Add(new BarLine(L.T("Dernière partie", "Last match", "Última partida"), L.N(last.ChestsOpened),
                L.T($"finie à {Sessions.EndUtc(last).ToLocalTime():HH:mm}", $"ended at {Sessions.EndUtc(last).ToLocalTime():HH:mm}", $"terminó a las {Sessions.EndUtc(last).ToLocalTime():HH:mm}")));
        }

        var sessionLabel = sessionRunning ? L.T("Session", "Session", "Sesión") : L.T("Dernière session", "Last session", "Última sesión");
        if (sessionStartUtc is { } start)
        {
            var inSession = d.Matches.Where(m => Sessions.InWindow(m, start)).ToList();
            lines.Add(new BarLine(sessionLabel, L.N(inSession.Sum(m => m.ChestsOpened) + liveChests), Detail(inSession)));
        }
        else
        {
            lines.Add(new BarLine(sessionLabel, "—"));
        }

        var todayStart = Sessions.TodayStartUtc(utcNow, TimeZoneInfo.Local);
        var today = d.Matches.Where(m => Sessions.InWindow(m, todayStart)).ToList();
        lines.Add(new BarLine(L.T("Aujourd'hui", "Today", "Hoy"), L.N(today.Sum(m => m.ChestsOpened) + liveChests), Detail(today)));

        var week = TreasureOdds.CurrentWeek(utcNow);
        var weekChests = d.Matches.Where(m => Sessions.EndUtc(m) >= week.StartUtc && Sessions.EndUtc(m) < week.EndUtc).Sum(m => m.ChestsOpened) + liveChests;
        var (chances, empty) = Chances(week, weekChests, total, obtained);

        return inProgress
            ? new BarView(L.N(total), BarState.Live, L.T("● EN DIRECT", "● LIVE", "● EN DIRECTO"), lines, chances, empty)
            : new BarView(L.N(total), BarState.Confirmed, L.T("✓ CONFIRMÉ (replay)", "✓ CONFIRMED (replay)", "✓ CONFIRMADO (repetición)"), lines, chances, empty);
    }

    /// <summary>
    /// Console : pas de partie en cours (aucune donnée pendant la partie). Les chiffres estimés sont préfixés « ≈ » ;
    /// « — » quand on ne sait pas (session au-delà du plafond de 1 000, journée pas encore publiée).
    /// </summary>
    public static BarView ForPs5(int? total, bool totalEstimated, int? today, bool todayEstimated, int session, bool sessionKnown,
        int weekChests, bool weekEstimated, int seasonChests, ObtainedStore obtained, DateTime utcNow)
    {
        static string Val(int? n, bool est) => n == null ? "—" : (est ? "≈ " : "") + L.N(n.Value);
        var lines = new List<BarLine>
        {
            new(L.T("Session", "Session", "Sesión"), sessionKnown ? "+" + L.N(session) : "—", L.T("depuis le lancement du compteur", "since the counter started", "desde que se abrió el contador")),
            new(L.T("Aujourd'hui", "Today", "Hoy"), today == null ? "—" : (todayEstimated ? "≈ +" : "+") + L.N(today.Value)),
        };
        var week = TreasureOdds.CurrentWeek(utcNow);
        var (chances, empty) = Chances(week, weekChests, seasonChests, obtained, approx: weekEstimated);
        var estimated = totalEstimated || (today != null && todayEstimated) || weekEstimated;
        var totalText = total == null ? "–" : Val(total, totalEstimated);
        return estimated
            ? new BarView(totalText, BarState.Estimate, L.T("≈ ESTIMATION", "≈ ESTIMATE", "≈ ESTIMACIÓN"), lines, chances, empty)
            : new BarView(totalText, BarState.EstimateConfirmed, L.T("✓ CONFIRMÉ (Epic)", "✓ CONFIRMED (Epic)", "✓ CONFIRMADO (Epic)"), lines, chances, empty);
    }

    /// <summary>≥ 10 % : « 63 % » ; ≥ 1 % : « 4,2 % » ; sinon 3 décimales « 0,092 % ».</summary>
    public static string Percent(double p)
    {
        var pct = p * 100;
        var format = pct >= 10 ? "0" : pct >= 1 ? "0.0" : "0.000";
        return pct.ToString(format, L.Culture) + " %";
    }

    private static (List<ChanceLine>, string?) Chances(Week week, int weekChests, int seasonChests, ObtainedStore obtained, bool approx = false)
    {
        var list = new List<ChanceLine>();
        foreach (var k in TreasureOdds.Kinds)
        {
            if (obtained.Has(week.Number, k.Kind)) continue;
            var chests = k.Weekly ? weekChests : seasonChests;
            var p = TreasureOdds.Chance(k.OneIn, chests);
            var name = week.Prizes.FirstOrDefault(x => x.Kind == k.Kind)?.Name;
            var scope = k.Weekly ? L.T("cette semaine", "this week", "esta semana") : L.T("toute la saison", "whole season", "toda la temporada");
            var odds = $"{(name != null ? name + " · " : "")}{L.T("1 sur", "1 in", "1 de")} {L.N(k.OneIn)} · {scope}";
            list.Add(new ChanceLine(k.Kind, TreasureOdds.KindLabel(k.Kind), (approx ? "≈ " : "") + Percent(p), Math.Min(1, p), odds));
        }
        return (list, list.Count == 0 ? L.T("Tout obtenu cette semaine", "Everything obtained this week", "Todo conseguido esta semana") : null);
    }

    private static string? Detail(List<MatchRecord> matches)
    {
        if (matches.Count == 0) return null;
        var avg = Math.Round((double)matches.Sum(m => m.ChestsOpened) / matches.Count, MidpointRounding.AwayFromZero);
        return $"{L.Count(matches.Count, "partie", "parties", "match", "matches", "partida", "partidas")} · {L.T("moy.", "avg.", "media")} {avg.ToString("0", L.Culture)}";
    }
}
