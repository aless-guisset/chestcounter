using System.Text.Json.Nodes;

namespace ChestCounter;

/// <summary>
/// Une partie console vue par le compteur : le profil Epic (XP, niveau) change d'un coup à la fin de chaque partie
/// (test du 13/11 : rien pendant la partie, tout à la sortie). Coffres : la réponse du joueur (téléphone ou console),
/// sinon ses appuis « +1 coffre » pendant la partie, sinon sa moyenne (estimation).
/// </summary>
public sealed class PlayedMatch
{
    public string Id { get; set; } = "";
    public DateTime EndedUtc { get; set; }
    /// <summary>Coffres donnés par le joueur, ou par Epic sous le plafond (Source = "epic"). Null = pas de réponse.</summary>
    public int? Chests { get; set; }
    /// <summary>Appuis sur « +1 coffre » au téléphone pendant la partie.</summary>
    public int Taps { get; set; }
    /// <summary>Coffres lus à l'écran pendant la partie (Chiaki, OBS…) ; null si l'écran n'était pas lu.</summary>
    public int? Seen { get; set; }
    /// <summary>Boîtes de munitions lues à l'écran.</summary>
    public int Ammo { get; set; }
    public string? Source { get; set; }
}

public static class ConsoleMatches
{
    /// <summary>Nombre de parties gardées (les plus récentes).</summary>
    public const int Keep = 300;

    /// <summary>« niveau:xp » du profil athena, ou null si absent.</summary>
    public static string? XpKey(JsonNode profile)
    {
        if (profile["stats"]?["attributes"] is not JsonObject a) return null;
        var level = a["level"] as JsonValue;
        var xp = a["xp"] as JsonValue;
        if (level == null || xp == null || !level.TryGetValue<long>(out var l) || !xp.TryGetValue<long>(out var x)) return null;
        return $"{l}:{x}";
    }

    /// <summary>Vrai si l'XP ou le niveau a changé depuis le relevé précédent : une partie vient de finir.</summary>
    public static bool Ended(string? previous, string? now) => previous != null && now != null && previous != now;

    /// <summary>Réponse du joueur (ou Epic), sinon lecture de l'écran, sinon appuis, sinon moyenne (estimée).</summary>
    public static (int Value, bool Estimated) Value(PlayedMatch m, double? average) =>
        m.Chests is int c ? (c, false) : m.Seen is int s ? (s, false) : m.Taps > 0 ? (m.Taps, false)
        : ((int)Math.Round(average ?? 0, MidpointRounding.AwayFromZero), true);

    public static (int Sum, bool Estimated) Sum(IEnumerable<PlayedMatch> matches, double? average)
    {
        var (sum, est) = (0, false);
        foreach (var m in matches)
        {
            var (v, e) = Value(m, average);
            sum += v;
            est |= e;
        }
        return (sum, est);
    }

    private static bool Known(PlayedMatch m) => m.Chests != null || m.Seen != null || m.Taps > 0;

    /// <summary>Moyenne de coffres par partie : celle des réponses dès qu'il y en a 3, sinon celle donnée (étalonnage / rythme).</summary>
    public static double? Average(IEnumerable<PlayedMatch> matches, double? fallback)
    {
        var known = matches.Where(Known).Select(m => Value(m, null).Value).ToList();
        if (known.Count >= 3 || (fallback == null && known.Count > 0)) return known.Average();
        return fallback;
    }

    /// <summary>
    /// Correction du total estimé (parties Epic × ratio). Jour déjà publié par Epic : la partie y est comptée au ratio,
    /// on remplace par le vrai chiffre s'il est connu. Jour pas encore publié : la partie manque, on l'ajoute.
    /// </summary>
    public static double TotalCorrection(IEnumerable<PlayedMatch> matches, double ratio, double? average, Func<DateTime, bool> dayPublished)
    {
        double c = 0;
        foreach (var m in matches)
        {
            if (dayPublished(m.EndedUtc))
            {
                if (Known(m)) c += Value(m, null).Value - ratio;
            }
            else
            {
                c += Value(m, average).Value;
            }
        }
        return c;
    }

    public static PlayedMatch AddEnded(List<PlayedMatch> list, DateTime endedUtc, int taps, int? epicDelta, int? seen = null, int ammo = 0)
    {
        var m = new PlayedMatch
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            EndedUtc = endedUtc,
            Taps = taps,
            Chests = epicDelta,
            Seen = seen,
            Ammo = ammo,
            Source = epicDelta != null ? "epic" : seen != null ? "screen" : null,
        };
        list.Add(m);
        if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
        return m;
    }
}
