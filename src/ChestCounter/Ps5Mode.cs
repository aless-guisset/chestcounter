using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// Mode PS5 / console (--ps5). Les replays console restent dans la console : à la place, on lit le compteur de coffres
/// que Fortnite garde sur le compte Epic (quêtes « Chasse au trésor », plafonnées à 1 000). Lecture seule du profil.
/// Les fichiers (auth.json, data.json) ont le même format que l'ancien outil Node (%APPDATA%\ChestCounterConsole),
/// qui est repris automatiquement au premier lancement.
/// </summary>
public static class Ps5Mode
{
    private const string Account = "https://account-public-service-prod.ol.epicgames.com/account/api";
    private const string Fortnite = "https://fortnite-public-service-prod11.ol.epicgames.com/fortnite/api";
    // Client « fortniteAndroidGameClient » (public, le seul qui peut créer un device auth).
    private const string ClientId = "3f69e56c7649492c8cc29f1af08a8a12";
    private const string ClientSecret = "b51ee9cb12234f50a69efa67ef53812e";
    private static readonly string Basic = "basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));
    private static readonly string RedirectUrl = $"https://www.epicgames.com/id/api/redirect?clientId={ClientId}&responseType=code";
    private static readonly string LoginUrl = "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(RedirectUrl);

    /// <summary>Le compteur de la Chasse au trésor s'arrête là.</summary>
    private const int Cap = 1000;
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(60);
    private static readonly Regex ChestRe = new("chest|coffre|treasure|delilah|relic", RegexOptions.IgnoreCase);
    private static readonly Regex PreferRe = new("treasure|delilah", RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Données (format identique à l'outil Node) -------------------------------------------------------------

    public sealed class AuthFile
    {
        public string AccountId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Secret { get; set; } = "";
        public string? DisplayName { get; set; }
    }

    public sealed class Baseline
    {
        public DateTime At { get; set; }
        public int Value { get; set; }
    }

    public sealed class Gain
    {
        public DateTime At { get; set; }
        public int Delta { get; set; }
        public int Total { get; set; }
    }

    public sealed class Ps5Data
    {
        public string? TrackId { get; set; }
        public Baseline? Baseline { get; set; }
        public int? Last { get; set; }
        public bool Manual { get; set; }
        public List<Gain> History { get; set; } = new();
        /// <summary>Cache des stats Epic : parties jouées depuis MatchStart jusqu'à la fin de chaque jour (yyyy-MM-dd).</summary>
        public DateTime? MatchStart { get; set; }
        /// <summary>Étalonnage donné une fois par le joueur : ses vrais chiffres de la saison à un instant.</summary>
        public Calibration? Calib { get; set; }
        public bool CalibAsked { get; set; }
        public Dictionary<string, int> MatchesPerUtcDay { get; set; } = new();
    }

    public sealed class Calibration
    {
        public DateTime At { get; set; }
        /// <summary>Parties jouées cette saison selon le joueur (0 = garder le chiffre d'Epic).</summary>
        public int Matches { get; set; }
        /// <summary>Coffres ouverts cette saison selon le joueur (null = inconnu).</summary>
        public int? Chests { get; set; }
        /// <summary>Ou : sa moyenne de coffres par partie (null = inconnue).</summary>
        public double? PerMatch { get; set; }
    }

    public sealed record Counter(string Id, string Quest, string Objective, int Value, string State);

    /// <summary>Palier de la Chasse au trésor atteint : Kind = "count" (coffres) ou "rarity" (coffres rares).</summary>
    public sealed record Milestone(string Kind, int Value, DateTime At);

    /// <summary>Ce qu'on reconstruit de la saison : paliers, coffres par jour (exacts ou estimés), estimations.</summary>
    public sealed class Season
    {
        public List<Milestone> Count { get; } = new();
        public List<Milestone> Rare { get; } = new();
        /// <summary>Coffres par jour vus en direct par le compteur (à la minute près).</summary>
        public Dictionary<DateTime, double> PerDayExact { get; } = new();
        /// <summary>Coffres par jour estimés (trous comblés d'après les parties jouées, ou au-delà du plafond).</summary>
        public Dictionary<DateTime, double> PerDayEstimated { get; } = new();
        /// <summary>Estimation au-delà du plafond via le rythme des coffres rares, et sa date.</summary>
        public int? Estimate { get; set; }
        public DateTime? EstimateAt { get; set; }
        /// <summary>Estimation à aujourd'hui au-delà du plafond : coffres par partie × parties jouées (stats Epic).</summary>
        public int? PaceEstimate { get; set; }
        public double PaceRatio { get; set; }
        public int MatchesBefore { get; set; }
        public int MatchesAfter { get; set; }
        /// <summary>Vrai si les parties jouées (stats Epic) ont servi à répartir les trous.</summary>
        public bool UsedMatches { get; set; }
        /// <summary>Parties jouées depuis le début (stats Epic), pour l'estimation de la session au-delà du plafond.</summary>
        public int? MatchesNow { get; set; }
        /// <summary>Correction des parties Epic : parties réelles (étalonnage) ÷ parties vues par Epic au même moment.</summary>
        public double MatchScale { get; set; } = 1;
        public Calibration? Calib { get; set; }
    }

    private sealed record Pace(DateTime At, double Ratio, int Before, int Now);
    private static Pace? _pace;
    private static DateTime _matchesRefreshed = DateTime.MinValue;

    // ---- Modèle de la saison --------------------------------------------------------------------------------------
    //
    // Points sûrs (temps, total) : début de la quête (0), chaque palier atteint, chaque gain vu par le compteur, et la
    // valeur actuelle tant qu'elle est sous le plafond. Entre deux points sûrs, les coffres sont répartis sur les jours
    // au prorata des parties jouées (stats Epic), ou linéairement dans le temps si les stats manquent. Deux gains vus à
    // moins de 15 min d'intervalle sont exacts. Au-delà du plafond : coffres par partie × parties jouées.
    // Marche pour n'importe quel compte : rien n'est propre à une saison ni à un joueur.

    private sealed record Point(DateTime At, int Value, bool Tracked);

    private static void ApplyModel(Season s, Counter? tracked, bool capped)
    {
        var points = new List<Point>();
        points.AddRange(s.Count.Select(m => new Point(m.At, m.Value, false)));
        if (_data.Baseline is { } b && b.Value < Cap) points.Add(new Point(b.At, b.Value, true));
        points.AddRange(_data.History.Where(h => h.Total <= Cap).Select(h => new Point(h.At, h.Total, true)));
        if (tracked != null && !capped && tracked.Value < Cap) points.Add(new Point(DateTime.UtcNow, tracked.Value, true));
        var cal = _data.Calib;
        if (points.Count == 0) return;
        var start = points.Min(p => p.At);
        _seasonStart = start;
        RefreshMatches(start);
        // Coffres au moment de l'étalonnage : donnés par le joueur, ou sa moyenne × ses parties.
        var realAtCal = cal == null ? 0 : cal.Matches > 0 ? cal.Matches : MatchesAt(cal.At, start);
        int? calChests = cal?.Chests ?? (cal?.PerMatch is double pm && realAtCal > 0 ? (int)Math.Round(pm * realAtCal) : null);
        if (capped && calChests is int cch && cch >= Cap) points.Add(new Point(cal!.At, cch, false)); // point sûr donné par le joueur
        points = points.OrderBy(p => p.At).ToList();
        var clean = new List<Point>();
        foreach (var p in points)
        {
            if (clean.Count > 0 && p.Value < clean[^1].Value) continue; // valeur incohérente (autre compteur) : ignorée
            clean.Add(p);
        }
        if (clean.Count == 0) return;
        s.Calib = cal;
        if (cal != null && cal.Matches > 0)
        {
            var epicAtCal = MatchesAt(cal.At, start);
            if (epicAtCal >= 1) s.MatchScale = cal.Matches / epicAtCal;
        }
        s.UsedMatches = _data.MatchesPerUtcDay.Count > 0;

        for (var i = 1; i < clean.Count; i++)
        {
            var (a, z) = (clean[i - 1], clean[i]);
            var delta = z.Value - a.Value;
            if (delta <= 0) continue;
            if (a.Tracked && z.Tracked && z.At - a.At <= TimeSpan.FromMinutes(15))
            {
                var day = z.At.ToLocalTime().Date;
                s.PerDayExact[day] = s.PerDayExact.GetValueOrDefault(day) + delta;
                continue;
            }
            Spread(s.PerDayEstimated, a.At, z.At, delta, start);
        }

        if (!capped) return;
        var last = clean[^1];
        double afterCap;
        if (cal != null && calChests is int cc && realAtCal > 0 && last.At == cal.At && last.Value == cc)
        {
            // Étalonné : coffres par partie = les tiens, parties depuis l'étalonnage corrigées par le même facteur.
            var ratio = cal.PerMatch ?? cc / realAtCal;
            var after = Math.Max(0, MatchesAt(DateTime.UtcNow, start) - MatchesAt(cal.At, start)) * s.MatchScale;
            s.PaceRatio = ratio;
            s.MatchesBefore = (int)Math.Round(realAtCal);
            s.MatchesAfter = (int)Math.Round(after);
            s.MatchesNow = s.MatchesBefore + s.MatchesAfter;
            afterCap = after * ratio;
        }
        else
        {
            var pace = ComputePace(s, start, s.Count.LastOrDefault(m => m.Value >= Cap)?.At ?? last.At);
            if (pace == null) return;
            // Mêmes coffres au total ; seules les parties affichées sont corrigées (et donc les coffres par partie).
            s.PaceRatio = pace.Ratio / s.MatchScale;
            s.MatchesBefore = (int)Math.Round(pace.Before * s.MatchScale);
            s.MatchesAfter = (int)Math.Round(Math.Max(0, pace.Now - pace.Before) * s.MatchScale);
            s.MatchesNow = s.MatchesBefore + s.MatchesAfter;
            afterCap = Math.Max(0, pace.Now - pace.Before) * pace.Ratio;
        }
        // Total = dernier point sûr (plafond ou étalonnage) + parties jouées depuis × coffres par partie.
        s.PaceEstimate = (int)(Math.Round((last.Value + afterCap) / 10) * 10);
        // Répartition par jour selon les parties de chaque jour, recalée pour que la somme des jours = le total.
        var raw = new List<(DateTime Day, double Matches)>();
        for (var day = last.At.ToLocalTime().Date; day <= DateTime.Now.Date; day = day.AddDays(1))
        {
            var from = Max(day, last.At.ToLocalTime());
            var to = Min(day.AddDays(1), DateTime.Now);
            raw.Add((day, Math.Max(0, MatchesAt(to.ToUniversalTime(), start) - MatchesAt(from.ToUniversalTime(), start))));
        }
        var rawSum = raw.Sum(r => r.Matches);
        foreach (var (day, n) in raw)
        {
            var part = rawSum > 0 ? afterCap * n / rawSum : afterCap / raw.Count;
            if (part > 0) s.PerDayEstimated[day] = s.PerDayEstimated.GetValueOrDefault(day) + part;
        }
    }

    private static DateTime? _seasonStart;

    public static string StatsUrlGg => "https://fortnite.gg/stats?player=" + Uri.EscapeDataString(_player ?? "");
    public static string StatsUrlTracker => "https://fortnitetracker.com/profile/all/" + Uri.EscapeDataString(_player ?? "");

    private static double? ParseNumber(string text)
    {
        var t = text.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.');
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
    }

    /// <summary>Écran d'étalonnage : le joueur donne une fois ses vrais chiffres de la saison (parties, puis coffres).</summary>
    private static void CalibrateScreen()
    {
        _data.CalibAsked = true;
        var epicMatches = _seasonStart is DateTime st ? (int)Math.Round(MatchesAt(DateTime.UtcNow, st)) : 0;
        var title = L.T("Étalonnage", "Calibration", "Calibración");

        // 1. Parties jouées
        int? matches = null;
        var clear = false;
        while (matches == null && !clear)
        {
            Frames.Invalidate();
            ConsoleUi.Screen(title, "1/2");
            ConsoleUi.Text(L.T("Combien de parties as-tu jouées cette saison ?", "How many matches have you played this season?", "¿Cuántas partidas has jugado esta temporada?"), ConsoleColor.White);
            ConsoleUi.Blank();
            ConsoleUi.Hint(L.T($"Epic en voit ≈ {epicMatches} (sans celles d'aujourd'hui, publiées demain). Ton vrai chiffre est sur fortnite.gg ou fortnitetracker.com (saison en cours), ou dans Fortnite → Carrière.",
                $"Epic sees ≈ {epicMatches} (without today's, published tomorrow). Your real number is on fortnite.gg or fortnitetracker.com (current season), or in Fortnite → Career.",
                $"Epic ve ≈ {epicMatches} (sin las de hoy, publicadas mañana). Tu cifra real está en fortnite.gg o fortnitetracker.com (temporada actual), o en Fortnite → Trayectoria."));
            if (_data.Calib is { } c)
            {
                ConsoleUi.Hint(L.T($"Étalonnage actuel ({c.At.ToLocalTime():dd'/'MM}) : {Describe(c)}.", $"Current calibration ({c.At.ToLocalTime():d MMM}): {Describe(c)}.", $"Calibración actual ({c.At.ToLocalTime():dd'/'MM}): {Describe(c)}."));
            }
            ConsoleUi.Blank();
            var options = new List<string>
            {
                L.T("Entrer mon nombre de parties", "Enter my number of matches", "Escribir mi número de partidas"),
                L.T("Voir mes parties sur fortnite.gg (navigateur)", "See my matches on fortnite.gg (browser)", "Ver mis partidas en fortnite.gg (navegador)"),
                L.T("Voir mes parties sur fortnitetracker.com (navigateur)", "See my matches on fortnitetracker.com (browser)", "Ver mis partidas en fortnitetracker.com (navegador)"),
                L.T($"Garder le chiffre d'Epic (≈ {epicMatches})", $"Keep Epic's number (≈ {epicMatches})", $"Mantener la cifra de Epic (≈ {epicMatches})"),
            };
            if (_data.Calib != null) options.Add(L.T("Effacer l'étalonnage", "Clear the calibration", "Borrar la calibración"));
            switch (ConsoleUi.Menu(options, 0, Keys.MenuHelp, escapeIndex: 3))
            {
                case 0:
                    if (ParseNumber(ConsoleUi.Input(L.T("Parties jouées cette saison : ", "Matches played this season: ", "Partidas jugadas esta temporada: "))) is double m && m >= 1)
                        matches = (int)Math.Round(m);
                    break;
                case 1: OpenUrl(StatsUrlGg); break;
                case 2: OpenUrl(StatsUrlTracker); break;
                case 3: matches = 0; break;
                default: clear = true; break;
            }
        }
        if (clear)
        {
            _data.Calib = null;
            Done(L.T("Étalonnage effacé.", "Calibration cleared.", "Calibración borrada."));
            return;
        }

        // 2. Coffres
        Frames.Invalidate();
        ConsoleUi.Screen(title, "2/2");
        ConsoleUi.Text(L.T("Et tes coffres ?", "And your chests?", "¿Y tus cofres?"), ConsoleColor.White);
        ConsoleUi.Blank();
        var auto = _season.PaceRatio > 0 ? _season.PaceRatio : 0;
        var choice = ConsoleUi.Menu(new[]
        {
            L.T("Entrer mon nombre total de coffres de la saison", "Enter my total chests this season", "Escribir mi total de cofres de la temporada"),
            L.T("Entrer ma moyenne de coffres par partie", "Enter my average chests per match", "Escribir mi media de cofres por partida"),
            L.T($"Je ne sais pas (0) : estimation automatique (≈ {auto:0} par partie)", $"I don't know (0): automatic estimate (≈ {auto:0} per match)", $"No lo sé (0): estimación automática (≈ {auto:0} por partida)"),
        }, 2, Keys.MenuHelp, escapeIndex: 2);
        int? chests = null;
        double? perMatch = null;
        if (choice == 0 && ParseNumber(ConsoleUi.Input(L.T("Coffres ouverts cette saison : ", "Chests opened this season: ", "Cofres abiertos esta temporada: "))) is double t && t >= 1)
            chests = (int)Math.Round(t);
        if (choice == 1 && ParseNumber(ConsoleUi.Input(L.T("Coffres par partie (ex. 12,5) : ", "Chests per match (e.g. 12.5): ", "Cofres por partida (ej. 12,5): "))) is double a && a > 0)
            perMatch = Math.Round(a, 2);

        _data.Calib = matches > 0 || chests != null || perMatch != null
            ? new Calibration { At = DateTime.UtcNow, Matches = matches ?? 0, Chests = chests, PerMatch = perMatch }
            : null;
        Done(_data.Calib == null ? L.T("Rien à enregistrer : estimation automatique.", "Nothing to save: automatic estimate.", "Nada que guardar: estimación automática.")
            : L.T($"Enregistré : {Describe(_data.Calib)}.", $"Saved: {Describe(_data.Calib)}.", $"Guardado: {Describe(_data.Calib)}."));
    }

    private static void Done(string message)
    {
        ConsoleUi.Blank();
        ConsoleUi.Text(message, ConsoleColor.Green);
        Thread.Sleep(1200);
        Save(DataPath, _data);
        _nextPoll = DateTime.MinValue;
        Frames.Invalidate();
    }

    /// <summary>« 200 parties · 12,5 coffres par partie » etc.</summary>
    public static bool HasChests(Calibration? c) => c != null && (c.Chests != null || c.PerMatch != null);

    public static string Describe(Calibration c)
    {
        var parts = new List<string>();
        parts.Add(c.Matches > 0 ? $"{L.N(c.Matches)} " + L.T("parties", "matches", "partidas") : L.T("parties d'Epic", "Epic's matches", "partidas de Epic"));
        if (c.Chests is int ch) parts.Add($"{L.N(ch)} " + L.T("coffres", "chests", "cofres"));
        else if (c.PerMatch is double pm) parts.Add(pm.ToString("0.##", L.Culture) + " " + L.T("coffres par partie", "chests per match", "cofres por partida"));
        else parts.Add(L.T("coffres estimés automatiquement", "chests estimated automatically", "cofres estimados automáticamente"));
        return string.Join(" · ", parts);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Répartit <paramref name="delta"/> coffres entre deux instants, jour par jour, au prorata des parties jouées.</summary>
    private static void Spread(Dictionary<DateTime, double> perDay, DateTime fromUtc, DateTime toUtc, int delta, DateTime start)
    {
        var from = fromUtc.ToLocalTime();
        var to = toUtc.ToLocalTime();
        var days = new List<(DateTime Day, double Matches, double Seconds)>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var s0 = Max(day, from);
            var s1 = Min(day.AddDays(1), to);
            if (s1 <= s0) continue;
            days.Add((day, Math.Max(0, MatchesAt(s1.ToUniversalTime(), start) - MatchesAt(s0.ToUniversalTime(), start)), (s1 - s0).TotalSeconds));
        }
        var totalMatches = days.Sum(d => d.Matches);
        var totalSeconds = days.Sum(d => d.Seconds);
        foreach (var (day, matches, seconds) in days)
        {
            var share = totalMatches > 0 ? matches / totalMatches : totalSeconds > 0 ? seconds / totalSeconds : 0;
            if (share > 0) perDay[day] = perDay.GetValueOrDefault(day) + delta * share;
        }
    }

    /// <summary>
    /// Parties jouées par jour, d'après les stats Epic. Epic les publie par <b>jour UTC</b>, vers 1 h du matin le
    /// lendemain : un jour n'apparaît dans une fenêtre de temps que si elle le couvre en entier. On demande donc chaque
    /// jour UTC [00:00, 24:00). Le jour en cours n'est pas encore publié. Cache dans data.json (les 2 derniers jours sont
    /// redemandés, toutes les 10 minutes au plus).
    /// </summary>
    private static void RefreshMatches(DateTime startUtc)
    {
        if (_data.MatchStart != startUtc)
        {
            _data.MatchStart = startUtc;
            _data.MatchesPerUtcDay.Clear();
            _matchesRefreshed = DateTime.MinValue;
        }
        if (DateTime.UtcNow - _matchesRefreshed < TimeSpan.FromMinutes(10)) return;
        var todayUtc = DateTime.UtcNow.Date;
        for (var d = DateTime.SpecifyKind(startUtc.Date, DateTimeKind.Utc); d < todayUtc; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            if (_data.MatchesPerUtcDay.ContainsKey(key) && d < todayUtc.AddDays(-2)) continue;
            var n = MatchesBetween(d, d.AddDays(1));
            if (n == null) return; // stats indisponibles : on réessaiera
            _data.MatchesPerUtcDay[key] = n.Value;
        }
        _matchesRefreshed = DateTime.UtcNow;
        Save(DataPath, _data);
    }

    /// <summary>
    /// Parties jouées entre le début et un instant (UTC) : jours UTC publiés, chacun réparti uniformément sur sa
    /// journée (Epic ne donne pas l'heure). Le jour du début compte à partir du début.
    /// </summary>
    private static double MatchesAt(DateTime utc, DateTime startUtc)
    {
        utc = utc.ToUniversalTime();
        if (utc <= startUtc) return 0;
        double sum = 0;
        // Les parties du jour UTC « d » sont placées sur la même date chez le joueur (journée locale de minuit à minuit) :
        // à une heure près, c'est le même jour, et ça évite de glisser des parties de la veille sur le lendemain.
        for (var d = startUtc.ToLocalTime().Date; d <= utc.ToLocalTime().Date; d = d.AddDays(1))
        {
            if (!_data.MatchesPerUtcDay.TryGetValue(d.ToString("yyyy-MM-dd"), out var n) || n == 0) continue;
            var dayStart = Max(DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime(), startUtc);
            var dayEnd = DateTime.SpecifyKind(d.AddDays(1), DateTimeKind.Local).ToUniversalTime();
            var span = (dayEnd - dayStart).TotalSeconds;
            sum += span <= 0 ? n : n * Math.Clamp((Min(utc, dayEnd) - dayStart).TotalSeconds / span, 0, 1);
        }
        return sum;
    }

    /// <summary>Jours UTC dont les parties ne sont pas encore publiées par Epic (aujourd'hui).</summary>
    public static bool TodayPending => !_data.MatchesPerUtcDay.ContainsKey(DateTime.UtcNow.Date.ToString("yyyy-MM-dd"));

    /// <summary>Coffres par partie, mesurés jusqu'au plafond (ou jusqu'à l'estimation par les coffres rares, plus longue).</summary>
    private static Pace? ComputePace(Season s, DateTime start, DateTime capAt)
    {
        var before = MatchesAt(capAt, start);
        var now = MatchesAt(DateTime.UtcNow, start);
        if (before < 1) return null;
        var ratio = Cap / before;
        if (s.Estimate is int rareEst && s.EstimateAt is DateTime rareAt)
        {
            var atRare = MatchesAt(rareAt, start);
            if (atRare > before) ratio = rareEst / atRare;
        }
        _pace = new Pace(DateTime.UtcNow, ratio, (int)Math.Round(before), (int)Math.Round(Math.Max(now, before)));
        return _pace;
    }

    private sealed class EpicException(HttpStatusCode status, string code, string message) : Exception($"{(int)status} {code} {message}".Trim())
    {
        public HttpStatusCode Status { get; } = status;
    }

    private static string _folder = "";
    private static string AuthPath => Path.Combine(_folder, "auth.json");
    private static string DataPath => Path.Combine(_folder, "data.json");
    private static string CsvPath => Path.Combine(_folder, "historique.csv");
    private static string ReportPath => Path.Combine(_folder, "report.html");
    private static bool _reportOpened;

    private static void WriteReport()
    {
        var session = _tracked != null && _sessionStart != null ? _tracked.Value - _sessionStart.Value : 0;
        ReportWriter.WritePs5(ReportPath, _player, _data, _counters, _tracked, Capped, session, _error, _season);
    }

    private static string? _token, _accountId, _player;
    private static DateTime _expires;

    // État affiché
    private static Ps5Data _data = new();
    private static List<Counter> _counters = new();
    private static Counter? _tracked;
    private static Season _season = new();
    private static int? _sessionStart;
    private static DateTime? _lastSync;
    private static string? _error;
    private static DateTime _nextPoll = DateTime.MinValue;

    /// <summary>--ps5-quests : écrit les attributs bruts des quêtes Chasse au trésor (dates de palier) dans ps5\\quests.txt.</summary>
    public static int DumpQuests(string dataDir)
    {
        _folder = Path.Combine(dataDir, "ps5");
        Directory.CreateDirectory(_folder);
        ImportFromNodeTool();
        if (!EnsureSession()) return 1;
        var profile = QueryProfile();
        var sb = new StringBuilder();
        if (profile["items"] is JsonObject items)
        {
            foreach (var (_, item) in items)
            {
                var tid = (string?)item?["templateId"] ?? "";
                if (tid.StartsWith("quest:", StringComparison.OrdinalIgnoreCase) && ChestRe.IsMatch(tid))
                {
                    var attrs = item!["attributes"]!.DeepClone().AsObject();
                    attrs.Remove("challenge_linked_quest_parent");
                    sb.AppendLine(tid + "  " + attrs.ToJsonString());
                }
            }
        }
        File.WriteAllText(Path.Combine(_folder, "quests.txt"), sb.ToString());
        return 0;
    }

    private const string StatsUrl = "https://statsproxy-public-service-live.ol.epicgames.com/statsproxy/api/statsv2/account/";

    /// <summary>Parties BR jouées entre deux instants (statsv2 avec fenêtre de temps), ou null si indisponible.</summary>
    public static int? MatchesBetween(DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            var s = Epic($"{StatsUrl}{_accountId}?startTime={new DateTimeOffset(fromUtc).ToUnixTimeSeconds()}&endTime={new DateTimeOffset(toUtc).ToUnixTimeSeconds()}", token: _token);
            if (s["stats"] is not JsonObject stats) return 0;
            return stats.Where(kv => kv.Key.StartsWith("br_matchesplayed_")).Sum(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0);
        }
        catch (Exception e)
        {
            ErrorLog.Write("PS5 stats", e);
            return null;
        }
    }

    /// <summary>--ps5-stats : parties jouées par jour (essai de la fenêtre de temps de statsv2), dans ps5\\stats.txt.</summary>
    public static int DumpStats(string dataDir)
    {
        _folder = Path.Combine(dataDir, "ps5");
        Directory.CreateDirectory(_folder);
        ImportFromNodeTool();
        if (!EnsureSession()) return 1;
        var sb = new StringBuilder();
        var todayUtc = DateTime.UtcNow.Date;
        sb.AppendLine("== parties par jour UTC (publiées par Epic vers 1 h le lendemain) ==");
        for (var d = todayUtc.AddDays(-14); d <= todayUtc; d = d.AddDays(1))
        {
            sb.AppendLine($"{d:yyyy-MM-dd}  {MatchesBetween(d, d.AddDays(1))?.ToString() ?? "erreur"}");
        }
        sb.AppendLine($"total sans fenêtre (vie entière)  {MatchesBetween(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow)}");
        try
        {
            var st = Epic($"{StatsUrl}{_accountId}?startTime={new DateTimeOffset(DateTime.UtcNow.AddDays(-30)).ToUnixTimeSeconds()}&endTime={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", token: _token);
            sb.AppendLine("== parties par mode, 30 derniers jours ==");
            foreach (var (k, v) in ((JsonObject)st["stats"]!).Where(kv => kv.Key.StartsWith("br_matchesplayed_")).OrderByDescending(kv => (int)kv.Value!))
                sb.AppendLine($"{v,5}  {k}");
        }
        catch (Exception e) { sb.AppendLine("erreur " + e.Message); }
        File.WriteAllText(Path.Combine(_folder, "stats.txt"), sb.ToString());
        return 0;
    }

    // ---- Moteur, piloté par l'écran principal de ChestCounter (plateforme « console ») -----------------------------

    private static Task<bool>? _polling;
    public static bool Initialized { get; private set; }
    public static string ReportFile => ReportPath;
    public static string Folder => _folder;
    public static bool HasAuth => Initialized && File.Exists(AuthPath);
    public static bool Busy => _polling != null;
    public static string? Player => _player;
    /// <summary>Proposé une fois, au premier lancement au-dessus du plafond.</summary>
    public static bool WantsCalibration => Capped && _data.Calib == null && !_data.CalibAsked;

    public static void Init(string dataDir)
    {
        if (Initialized) return;
        _folder = Path.Combine(dataDir, "ps5");
        Directory.CreateDirectory(_folder);
        ImportFromNodeTool();
        _data = Load<Ps5Data>(DataPath) ?? new Ps5Data();
        _player = Load<AuthFile>(AuthPath)?.DisplayName;
        Initialized = true;
    }

    /// <summary>À appeler souvent depuis la boucle principale. Retourne vrai si l'écran doit être redessiné.</summary>
    public static bool Tick(bool openReportOnFirstSync)
    {
        if (!HasAuth) return false;
        if (_polling == null && DateTime.Now >= _nextPoll)
        {
            _polling = Task.Run(Poll);
            return true;
        }
        if (_polling is not { IsCompleted: true }) return false;
        var ok = _polling.Result;
        _polling = null;
        _nextPoll = DateTime.Now + PollEvery;
        WriteReport();
        UpdateOverlay();
        if (!ok)
        {
            Say(L.T("La connexion Epic n'est plus valide : choisis « Se connecter à Epic ».", "The Epic sign-in is no longer valid: choose \"Sign in to Epic\".", "La conexión Epic ya no es válida: elige «Conectarse a Epic»."), ConsoleColor.Yellow);
        }
        else if (openReportOnFirstSync && !_reportOpened)
        {
            _reportOpened = true;
            OpenUrl(ReportPath);
        }
        return true;
    }

    public static void RefreshNow()
    {
        if (_polling == null) _nextPoll = DateTime.MinValue;
    }

    public static bool Login()
    {
        var ok = LoginScreen();
        Frames.Invalidate();
        if (ok) RefreshNow();
        return ok;
    }

    public static void SignOut()
    {
        _polling?.Wait();
        _polling = null;
        Logout();
        _tracked = null;
        _counters = new();
        _season = new();
        Say(L.T("Déconnecté d'Epic (connexion révoquée).", "Signed out of Epic (sign-in revoked).", "Sesión de Epic cerrada (conexión revocada)."), ConsoleColor.Yellow);
    }

    public static void OpenReport()
    {
        WriteReport();
        OpenUrl(ReportPath);
    }

    public static void Calibrate()
    {
        if (!Busy) CalibrateScreen();
    }

    public static void ChooseCounterScreen()
    {
        if (!Busy) ChooseCounter();
    }

    /// <summary>Lignes de l'accueil (plateforme console).</summary>
    public static void DrawHome(Action<string, Seg[]> row)
    {
        var mut = ConsoleColor.Gray;
        if (!HasAuth)
        {
            row(L.T("Compte", "Account", "Cuenta"), new[] { new Seg(L.T("pas connecté à Epic", "not signed in to Epic", "sin conexión a Epic"), ConsoleColor.DarkYellow) });
            row(L.T("État", "Status", "Estado"), new[] { new Seg(L.T("Choisis « Se connecter à Epic » ci-dessous (une seule fois).", "Choose \"Sign in to Epic\" below (only once).", "Elige «Conectarse a Epic» abajo (una sola vez)."), ConsoleColor.DarkYellow) });
            return;
        }
        row(L.T("Compte", "Account", "Cuenta"), new[] { new Seg(_player ?? "…", ConsoleColor.White), new Seg("   " + L.T("console · compte Epic", "console · Epic account", "consola · cuenta Epic"), ConsoleColor.DarkGray) });
        var next = _nextPoll > DateTime.Now ? (int)(_nextPoll - DateTime.Now).TotalSeconds : 0;
        row(L.T("État", "Status", "Estado"), Busy ? new[] { new Seg(L.T("synchro avec Epic…", "syncing with Epic…", "sincronizando con Epic…"), ConsoleColor.Cyan) }
            : _error != null ? new[] { new Seg("✖ " + _error, ConsoleColor.Red) }
            : _lastSync != null ? new[] { new Seg("● " + L.T("connecté", "connected", "conectado"), ConsoleColor.Green), new Seg(L.T($"  ·  synchro {_lastSync:HH:mm:ss}, prochaine dans {next} s", $"  ·  synced {_lastSync:HH:mm:ss}, next in {next} s", $"  ·  sincronizado {_lastSync:HH:mm:ss}, próxima en {next} s"), mut) }
            : new[] { new Seg("…", mut) });
        var total = _tracked?.Value;
        if (total == null) return;
        if (Capped && _season.PaceEstimate is int est)
        {
            row(L.T("Total", "Total", "Total"), new[] { new Seg("≈ " + L.Count(est, "coffre", "chest", "cofre"), ConsoleColor.Yellow),
                new Seg($"  ·  Epic {L.N(total.Value)}+  ·  " + L.T($"≈ {_season.PaceRatio:0.#} par partie", $"≈ {_season.PaceRatio:0.#} per match", $"≈ {_season.PaceRatio:0.#} por partida"), mut) });
        }
        else
        {
            row(L.T("Total", "Total", "Total"), new[] { new Seg(L.Count(total.Value, "coffre", "chest", "cofre"), ConsoleColor.Yellow), new Seg("  ·  " + L.T("exact, d'après Epic", "exact, from Epic", "exacto, según Epic"), mut) });
        }
        var today = DateTime.Now.Date;
        var todayExact = _season.PerDayExact.GetValueOrDefault(today);
        var todayEst = _season.PerDayEstimated.GetValueOrDefault(today);
        var session = _sessionStart != null ? total.Value - _sessionStart.Value : 0;
        if (Capped)
        {
            row(L.T("Aujourd'hui", "Today", "Hoy"), new[] { new Seg(TodayPending && todayExact < 0.5 ? "—" : "≈ " + L.Count((int)Math.Round(todayExact + todayEst), "coffre", "chest", "cofre"), ConsoleColor.Yellow),
                new Seg("  ·  " + L.T("au-delà de 1 000, Epic publie tes parties le lendemain vers 1 h", "beyond 1,000, Epic publishes your matches the next day around 1 am", "más allá de 1000, Epic publica tus partidas al día siguiente hacia la 1 h"), ConsoleColor.DarkGray) });
        }
        else
        {
            row(L.T("Aujourd'hui", "Today", "Hoy"), new[] { new Seg(L.Count((int)Math.Round(todayExact + todayEst), "coffre", "chest", "cofre"), ConsoleColor.Yellow) });
            row(L.T("Session", "Session", "Sesión"), new[] { new Seg(L.Count(session, "coffre", "chest", "cofre"), ConsoleColor.Yellow) });
        }
        if (_season.Count.Count > 1)
        {
            row(L.T("Paliers", "Stages", "Etapas"), new[] { new Seg(string.Join("  ", _season.Count.Skip(1).Select(m => $"{L.N(m.Value)} ({m.At.ToLocalTime():dd'/'MM})")), mut) });
        }
        if (OperatingSystem.IsWindows())
        {
            var rec = AudioWatch.Current;
            row(L.T("Son", "Sound", "Sonido"), rec != null
                ? new[] { new Seg("● " + L.T($"enregistrement de {AudioWatch.TargetName}", $"recording {AudioWatch.TargetName}", $"grabando {AudioWatch.TargetName}"), ConsoleColor.Red), new Seg($"  ·  {(int)rec.Duration.TotalMinutes} min", mut) }
                : new[] { new Seg(L.T("en attente de PS Remote Play (lance-le pour la détection en direct)", "waiting for PS Remote Play (start it for live detection)", "esperando PS Remote Play (ábrelo para la detección en directo)"), ConsoleColor.DarkGray) });
        }
        if (_data.Calib is { } c)
        {
            row(L.T("Étalonnage", "Calibration", "Calibración"), new[] { new Seg(Describe(c) + $"  ({c.At.ToLocalTime():dd'/'MM})", mut) });
        }
    }

    // ---- Reprise de l'outil Node ------------------------------------------------------------------------------

    private static void ImportFromNodeTool()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounterConsole");
        foreach (var name in new[] { "auth.json", "data.json", "historique.csv" })
        {
            var src = Path.Combine(old, name);
            var dst = Path.Combine(_folder, name);
            try
            {
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
            catch { /* rien de grave : on se reconnectera */ }
        }
    }

    // ---- Connexion ----------------------------------------------------------------------------------------------

    private static bool LoginScreen()
    {
        ConsoleUi.Screen(L.T("Connexion à ton compte Epic", "Sign in to your Epic account", "Conexión a tu cuenta Epic"), "PS5");
        ConsoleUi.Text(L.T(
            "Une seule fois. Sur console, les replays restent dans la PS5 : le compteur lit à la place\nle compteur de coffres que Fortnite garde sur ton compte Epic (lecture seule).",
            "Only once. On console, replays stay on the PS5: instead, the counter reads the chest counter\nFortnite keeps on your Epic account (read only).",
            "Solo una vez. En consola, las repeticiones se quedan en la PS5: el contador lee en su lugar\nel contador de cofres que Fortnite guarda en tu cuenta Epic (solo lectura)."));
        ConsoleUi.Blank();
        ConsoleUi.Text(L.T(
            "1. Ton navigateur s'ouvre : connecte-toi avec le compte Epic de ta PS5.\n2. Tu arrives sur une page avec  \"authorizationCode\":\"a1b2c3…\"\n3. Copie ce code (ou toute la page) et colle-le ici (clic droit = coller).",
            "1. Your browser opens: sign in with your PS5's Epic account.\n2. You land on a page with  \"authorizationCode\":\"a1b2c3…\"\n3. Copy this code (or the whole page) and paste it here (right click = paste).",
            "1. Se abre tu navegador: conéctate con la cuenta Epic de tu PS5.\n2. Llegas a una página con  \"authorizationCode\":\"a1b2c3…\"\n3. Copia ese código (o toda la página) y pégalo aquí (clic derecho = pegar)."));
        ConsoleUi.Blank();
        ConsoleUi.Hint(L.T("Si rien ne s'ouvre : ", "If nothing opens: ", "Si no se abre nada: ") + LoginUrl);
        ConsoleUi.Blank();
        OpenUrl(LoginUrl);
        while (true)
        {
            var input = ConsoleUi.Input(L.T("Code (vide = quitter) : ", "Code (empty = quit): ", "Código (vacío = salir): ")).Trim();
            if (input.Length == 0) return false;
            var code = Regex.Match(input, "[0-9a-fA-F]{32}").Value;
            if (code.Length == 0)
            {
                ConsoleUi.Text(L.T("Je ne trouve pas de code (32 caractères). Réessaie.", "No code found (32 characters). Try again.", "No encuentro el código (32 caracteres). Inténtalo de nuevo."), ConsoleColor.Red);
                continue;
            }
            try
            {
                var t = Epic($"{Account}/oauth/token", form: new() { ["grant_type"] = "authorization_code", ["code"] = code, ["token_type"] = "eg1" });
                var token = (string)t["access_token"]!;
                var accountId = (string)t["account_id"]!;
                var d = Epic($"{Account}/public/account/{accountId}/deviceAuth", method: HttpMethod.Post, token: token);
                Save(AuthPath, new AuthFile
                {
                    AccountId = (string)d["accountId"]!,
                    DeviceId = (string)d["deviceId"]!,
                    Secret = (string)d["secret"]!,
                    DisplayName = (string?)t["displayName"],
                });
                SetSession(t);
                ConsoleUi.Text(L.T($"Connecté : {_player}. Tu n'auras plus à le refaire.", $"Signed in: {_player}. You won't have to do it again.", $"Conectado: {_player}. No tendrás que hacerlo otra vez."), ConsoleColor.Green);
                Thread.Sleep(1500);
                return true;
            }
            catch (Exception e)
            {
                ConsoleUi.Text(L.T($"Epic a refusé ce code ({e.Message}). Un code ne sert qu'une fois : recharge la page et colle le nouveau.",
                    $"Epic refused this code ({e.Message}). A code works only once: reload the page and paste the new one.",
                    $"Epic rechazó este código ({e.Message}). Un código sirve una sola vez: recarga la página y pega el nuevo."), ConsoleColor.Red);
                ConsoleUi.Hint(RedirectUrl);
            }
        }
    }

    private static void SetSession(JsonNode t)
    {
        _token = (string)t["access_token"]!;
        _accountId = (string)t["account_id"]!;
        _player = (string?)t["displayName"] ?? _player;
        _expires = DateTime.TryParse((string?)t["expires_at"], out var e) ? e.ToUniversalTime() : DateTime.UtcNow.AddHours(1);
    }

    /// <summary>Jeton valide, ou false si la connexion enregistrée n'est plus acceptée (il faut se reconnecter).</summary>
    private static bool EnsureSession()
    {
        if (_token != null && _expires - DateTime.UtcNow > TimeSpan.FromMinutes(5)) return true;
        var auth = Load<AuthFile>(AuthPath);
        if (auth == null) return false;
        try
        {
            _player ??= auth.DisplayName;
            SetSession(Epic($"{Account}/oauth/token", form: new()
            {
                ["grant_type"] = "device_auth",
                ["account_id"] = auth.AccountId,
                ["device_id"] = auth.DeviceId,
                ["secret"] = auth.Secret,
                ["token_type"] = "eg1",
            }));
            return true;
        }
        catch (EpicException e) when ((int)e.Status is >= 400 and < 500)
        {
            File.Delete(AuthPath);
            _token = null;
            return false;
        }
    }

    private static void Logout()
    {
        var auth = Load<AuthFile>(AuthPath);
        if (auth != null && EnsureSession())
        {
            try { Epic($"{Account}/public/account/{auth.AccountId}/deviceAuth/{auth.DeviceId}", method: HttpMethod.Delete, token: _token); } catch { }
            try { Epic($"{Account}/oauth/sessions/kill/{_token}", method: HttpMethod.Delete, token: _token); } catch { }
        }
        try { File.Delete(AuthPath); } catch { }
        _token = null;
        _player = null;
    }

    private static JsonNode Epic(string url, HttpMethod? method = null, string? token = null, Dictionary<string, string>? form = null, string? json = null)
    {
        using var req = new HttpRequestMessage(method ?? (form != null || json != null ? HttpMethod.Post : HttpMethod.Get), url);
        if (form != null)
        {
            req.Content = new FormUrlEncodedContent(form);
            req.Headers.TryAddWithoutValidation("Authorization", Basic);
        }
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
        using var res = Http.Send(req);
        var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        JsonNode? body = null;
        try { body = text.Length > 0 ? JsonNode.Parse(text) : null; } catch { }
        if (!res.IsSuccessStatusCode)
        {
            throw new EpicException(res.StatusCode, (string?)body?["errorCode"] ?? "", (string?)body?["errorMessage"] ?? "");
        }
        return body ?? new JsonObject();
    }

    // ---- Compteurs ----------------------------------------------------------------------------------------------

    private static JsonNode QueryProfile()
    {
        string url() => $"{Fortnite}/game/v2/profile/{_accountId}/client/QueryProfile?profileId=athena&rvn=-1";
        try
        {
            return Epic(url(), token: _token, json: "{}")["profileChanges"]![0]!["profile"]!;
        }
        catch (EpicException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            _token = null;
            if (!EnsureSession()) throw;
            return Epic(url(), token: _token, json: "{}")["profileChanges"]![0]!["profile"]!;
        }
    }

    public static List<Counter> FindCounters(JsonNode profile)
    {
        var list = new List<Counter>();
        if (profile["items"] is JsonObject items)
        {
            foreach (var (_, item) in items)
            {
                var tid = (string?)item?["templateId"] ?? "";
                if (!tid.StartsWith("quest:", StringComparison.OrdinalIgnoreCase) || item?["attributes"] is not JsonObject attrs) continue;
                var state = attrs["quest_state"]?.ToString() ?? "";
                foreach (var (k, v) in attrs)
                {
                    if (!k.StartsWith("completion_") || v is not JsonValue jv || !jv.TryGetValue<int>(out var n)) continue;
                    if (!ChestRe.IsMatch(k) && !ChestRe.IsMatch(tid)) continue;
                    list.Add(new Counter($"{tid}|{k}", tid[6..], k[11..], n, state));
                }
            }
        }
        return list.OrderByDescending(c => c.Value).ToList();
    }

    /// <summary>La Chasse au trésor en priorité ; si elle est bloquée au plafond et qu'un autre compteur est plus haut, celui-là.</summary>
    public static Counter? AutoPick(List<Counter> counters)
    {
        var pref = counters.FirstOrDefault(c => PreferRe.IsMatch(c.Id));
        var best = counters.FirstOrDefault();
        if (pref != null && pref.Value >= Cap && best != null && best.Value > pref.Value) return best;
        return pref ?? best;
    }

    /// <summary>Nom lisible d'une quête : quest_s43_treasurehunt_count_05 → « Chasse au trésor · coffres · palier 5 ».</summary>
    public static string Pretty(string quest)
    {
        var m = Regex.Match(quest, @"treasurehunt_(count|rarity|sprites|map)_0*(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return quest;
        var what = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "count" => L.T("coffres", "chests", "cofres"),
            "rarity" => L.T("coffres rares", "rare chests", "cofres raros"),
            "sprites" => L.T("sprites", "sprites", "sprites"),
            _ => L.T("cartes au trésor", "treasure maps", "mapas del tesoro"),
        };
        return $"{L.T("Chasse au trésor", "Treasure Hunt", "Caza del tesoro")} · {what} · {L.T("palier", "stage", "etapa")} {m.Groups[2].Value}";
    }

    private static readonly Regex TierRe = new(@"treasurehunt_(count|rarity)_0*(\d+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Dates des paliers : le palier N est atteint quand le palier N+1 est créé (creation_time), sinon à sa
    /// validation (last_state_change_time). Le palier 1 créé = début de la quête (0 coffre).
    /// </summary>
    public static Season BuildSeason(JsonNode profile)
    {
        var season = new Season();
        var tiers = new List<(string Kind, int Tier, int Value, string State, DateTime Created, DateTime? Changed)>();
        if (profile["items"] is JsonObject items)
        {
            foreach (var (_, item) in items)
            {
                var tid = (string?)item?["templateId"] ?? "";
                var m = TierRe.Match(tid);
                if (!m.Success || item?["attributes"] is not JsonObject a) continue;
                var value = a.Where(kv => kv.Key.StartsWith("completion_")).Select(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0).DefaultIfEmpty(0).Max();
                if (!DateTime.TryParse((string?)a["creation_time"], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var created)) continue;
                DateTime? changed = DateTime.TryParse((string?)a["last_state_change_time"], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var c) ? c : null;
                tiers.Add((m.Groups[1].Value.ToLowerInvariant(), int.Parse(m.Groups[2].Value), value, a["quest_state"]?.ToString() ?? "", created, changed));
            }
        }
        foreach (var kind in new[] { "count", "rarity" })
        {
            var list = tiers.Where(t => t.Kind == kind).OrderBy(t => t.Tier).ToList();
            if (list.Count == 0) continue;
            var target = kind == "count" ? season.Count : season.Rare;
            target.Add(new Milestone(kind, 0, list[0].Created));
            for (var i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (!t.State.Equals("Claimed", StringComparison.OrdinalIgnoreCase)) continue;
                var at = i + 1 < list.Count ? list[i + 1].Created : t.Changed ?? t.Created;
                target.Add(new Milestone(kind, t.Value, at));
            }
        }

        // Estimation au-delà de 1 000 : nombre de coffres par coffre rare au moment des 1 000, appliqué au dernier palier rare.
        var thousand = season.Count.LastOrDefault(m => m.Value >= Cap);
        var lastRare = season.Rare.LastOrDefault();
        if (thousand != null && lastRare != null && lastRare.Value > 0 && lastRare.At > thousand.At)
        {
            var rareAtThousand = Interpolate(season.Rare, thousand.At);
            if (rareAtThousand > 0)
            {
                var est = Cap * lastRare.Value / rareAtThousand;
                if (est > Cap)
                {
                    season.Estimate = (int)(Math.Round(est / 10) * 10);
                    season.EstimateAt = lastRare.At;
                }
            }
        }
        return season;
    }

    private static double Interpolate(List<Milestone> points, DateTime at)
    {
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].At < at) continue;
            var (a, b) = (points[i - 1], points[i]);
            var span = (b.At - a.At).TotalSeconds;
            return span <= 0 ? b.Value : a.Value + (b.Value - a.Value) * (at - a.At).TotalSeconds / span;
        }
        return points.Count > 0 ? points[^1].Value : 0;
    }

    private static bool Capped => _tracked != null && _tracked.Value >= Cap && PreferRe.IsMatch(_tracked.Id);

    /// <summary>Une vérification. Retourne false si une nouvelle connexion est nécessaire.</summary>
    private static bool Poll()
    {
        try
        {
            if (!EnsureSession()) return false;
            var profile = QueryProfile();
            _counters = FindCounters(profile);
            var c = _counters.FirstOrDefault(x => x.Id == _data.TrackId);
            if (c != null && !_data.Manual && c.Value >= Cap)
            {
                var better = AutoPick(_counters);
                if (better != null && better.Id != c.Id)
                {
                    Add(L.T($"{c.Quest} est bloqué à {c.Value} : bascule sur {better.Quest}.", $"{c.Quest} is stuck at {c.Value}: switching to {better.Quest}.", $"{c.Quest} está bloqueado en {c.Value}: cambio a {better.Quest}."), ConsoleColor.Yellow);
                    c = null;
                }
            }
            if (c == null)
            {
                c = AutoPick(_counters);
                if (c == null)
                {
                    _error = L.T("Aucun compteur de coffres sur ce compte (Chasse au trésor pas commencée ?)", "No chest counter on this account (Treasure Hunt not started?)", "Ningún contador de cofres en esta cuenta (¿Caza del tesoro sin empezar?)");
                    _lastSync = DateTime.Now;
                    return true;
                }
                if (_data.TrackId != null) Add(L.T($"Nouveau compteur suivi : {c.Quest}", $"Now tracking: {c.Quest}", $"Nuevo contador: {c.Quest}"), ConsoleColor.Cyan);
                _data.TrackId = c.Id;
                _data.Baseline = null;
            }
            if (_data.Baseline == null || c.Value < _data.Baseline.Value)
            {
                _data.Baseline = new Baseline { At = DateTime.UtcNow, Value = c.Value };
                _data.Last = c.Value;
            }
            _sessionStart ??= c.Value;
            var prev = _data.Last ?? c.Value;
            if (c.Value > prev)
            {
                var g = new Gain { At = DateTime.UtcNow, Delta = c.Value - prev, Total = c.Value };
                _data.History.Add(g);
                AppendCsv(g);
                Add($"+{g.Delta} " + L.Count(g.Delta, "coffre", "chest", "cofre") + $"  →  {g.Total}", ConsoleColor.Green);
            }
            _data.Last = c.Value;
            _tracked = c;
            var season = BuildSeason(profile);
            try { ApplyModel(season, c, Capped); } catch (Exception e) { ErrorLog.Write("PS5 model", e); }
            _season = season;
            Save(DataPath, _data);
            _error = null;
            _lastSync = DateTime.Now;
        }
        catch (Exception e)
        {
            _error = e.Message;
            Add(L.T("Erreur : ", "Error: ", "Error: ") + e.Message, ConsoleColor.Red);
            ErrorLog.Write("PS5", e);
        }
        return true;
    }

    private static void AppendCsv(Gain g)
    {
        try
        {
            if (!File.Exists(CsvPath)) File.WriteAllText(CsvPath, "date;heure;coffres_gagnes;total_saison\n");
            var t = g.At.ToLocalTime();
            File.AppendAllText(CsvPath, $"{t:yyyy-MM-dd};{t:HH:mm:ss};{g.Delta};{g.Total}\n");
        }
        catch { }
    }

    private static void Add(string text, ConsoleColor color) => Say(text, color);

    /// <summary>Une ligne dans l'activité de l'écran principal.</summary>
    private static void Say(string text, ConsoleColor color)
    {
        lock (Console.Out)
        {
            try { Console.ForegroundColor = color; } catch { }
            Console.WriteLine(text);
            try { Console.ResetColor(); } catch { }
        }
    }

    // ---- Overlay (fenêtre de ChestCounter, alimentée par le compte Epic) ---------------------------------------

    public static void UpdateOverlay()
    {
        if (!OperatingSystem.IsWindows() || !NativeOverlay.IsRunning) return;
        var total = _tracked?.Value;
        var today = DateTime.Now.Date;
        var todayExact = _season.PerDayExact.GetValueOrDefault(today);
        var todayEst = _season.PerDayEstimated.GetValueOrDefault(today);
        var session = _tracked != null && _sessionStart != null ? _tracked.Value - _sessionStart.Value : 0;
        var sessionText = "+" + L.N(session);
        if (Capped)
        {
            // Au-delà du plafond, Epic ne publie les parties que le lendemain : la session ne peut pas être suivie en direct.
            sessionText = "—";
        }
        var totalText = Capped && _season.PaceEstimate is int est ? "≈" + L.N(est) : total == null ? "–" : L.N(total.Value);
        var todayText = Capped && TodayPending && todayExact < 0.5 ? "—"
            : (todayEst >= 0.5 ? "≈ +" : "+") + L.N((int)Math.Round(todayExact + todayEst));
        var epicText = total == null ? "–" : L.N(total.Value) + (Capped ? "+" : "");
        var rows = new List<OverlayRow>
        {
            new(L.T("Aujourd'hui", "Today", "Hoy"), todayText),
            new(L.T("Session", "Session", "Sesión"), sessionText),
            new("Epic", epicText),
        };
        NativeOverlay.Update(new OverlayView(totalText, null, L.T("cette saison", "this season", "esta temporada"), false, "PS5", rows,
            Treasures: epicText, Session: sessionText, Last: todayText, LastLabel: L.T("Aujourd'hui", "Today", "Hoy"), ThirdLabel: "Epic"));
        // Test : CHESTCOUNTER_OVERLAY_SNAPSHOT=<fichier.png> enregistre l'overlay tel qu'il est dessiné.
        if (Environment.GetEnvironmentVariable("CHESTCOUNTER_OVERLAY_SNAPSHOT") is { Length: > 0 } snap)
        {
            Thread.Sleep(500);
            NativeOverlay.Snapshot(snap);
        }
    }

    private static void ChooseCounter()
    {
        if (_counters.Count == 0) return;
        Frames.Invalidate();
        ConsoleUi.Screen(L.T("Compteur à suivre", "Counter to track", "Contador a seguir"), "PS5");
        var options = _counters.Select(c => $"{c.Value,6}  {c.Quest}  [{c.State}]").Append(L.T("Choix automatique", "Automatic", "Automático")).ToList();
        var current = Math.Max(0, _counters.FindIndex(c => c.Id == _data.TrackId));
        var i = ConsoleUi.Menu(options, current, Keys.MenuHelp, escapeIndex: -1);
        if (i >= 0 && i < _counters.Count)
        {
            _data.TrackId = _counters[i].Id;
            _data.Manual = true;
            _data.Baseline = null;
        }
        else if (i == _counters.Count)
        {
            _data.TrackId = null;
            _data.Manual = false;
            _data.Baseline = null;
        }
        Save(DataPath, _data);
        _nextPoll = DateTime.MinValue;
        Frames.Invalidate();
    }

    // ---- Utilitaires --------------------------------------------------------------------------------------------

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; } catch { return null; }
    }

    private static void Save<T>(string path, T value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }
}
