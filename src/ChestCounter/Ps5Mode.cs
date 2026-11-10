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
        public Dictionary<string, int> MatchDays { get; set; } = new();
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
    }

    private sealed record Pace(DateTime At, double Ratio, int Before, int Now);
    private static Pace? _pace;
    private static DateTime _matchesRefreshed = DateTime.MinValue;
    private static int? _matchesToday;

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
        points = points.OrderBy(p => p.At).ToList();
        var clean = new List<Point>();
        foreach (var p in points)
        {
            if (clean.Count > 0 && p.Value < clean[^1].Value) continue; // valeur incohérente (autre compteur) : ignorée
            clean.Add(p);
        }
        if (clean.Count == 0) return;

        var start = clean[0].At;
        RefreshMatches(start);
        s.UsedMatches = _data.MatchDays.Count > 0;

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
        var pace = ComputePace(s, start, s.Count.LastOrDefault(m => m.Value >= Cap)?.At ?? last.At);
        if (pace == null) return;
        s.PaceRatio = pace.Ratio;
        s.MatchesBefore = pace.Before;
        s.MatchesAfter = Math.Max(0, pace.Now - pace.Before);
        // Total = dernier point sûr (le plafond) + parties jouées depuis × coffres par partie.
        var afterCap = s.MatchesAfter * pace.Ratio;
        s.PaceEstimate = (int)(Math.Round((last.Value + afterCap) / 10) * 10);
        // Répartition par jour selon les parties de chaque jour, recalée pour que la somme des jours = le total.
        var raw = new List<(DateTime Day, double Matches)>();
        for (var day = last.At.ToLocalTime().Date; day <= DateTime.Now.Date; day = day.AddDays(1))
        {
            var from = Max(day, last.At.ToLocalTime());
            var to = Min(day.AddDays(1), DateTime.Now);
            raw.Add((day, Math.Max(0, MatchesAt(to, start) - MatchesAt(from, start))));
        }
        var rawSum = raw.Sum(r => r.Matches);
        foreach (var (day, n) in raw)
        {
            var part = rawSum > 0 ? afterCap * n / rawSum : afterCap / raw.Count;
            if (part > 0) s.PerDayEstimated[day] = s.PerDayEstimated.GetValueOrDefault(day) + part;
        }
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
            days.Add((day, Math.Max(0, MatchesAt(s1, start) - MatchesAt(s0, start)), (s1 - s0).TotalSeconds));
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
    /// Parties jouées depuis <paramref name="start"/> jusqu'à la fin de chaque jour (stats Epic). Les jours de plus de
    /// 2 jours sont gardés en cache (data.json) : Epic les range par blocs de quelques heures, les récents peuvent encore bouger.
    /// </summary>
    private static void RefreshMatches(DateTime start)
    {
        if (_data.MatchStart != start)
        {
            _data.MatchStart = start;
            _data.MatchDays.Clear();
            _matchesRefreshed = DateTime.MinValue;
        }
        if (DateTime.UtcNow - _matchesRefreshed < TimeSpan.FromMinutes(10)) return;
        var today = DateTime.Now.Date;
        for (var day = start.ToLocalTime().Date; day < today; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            if (_data.MatchDays.ContainsKey(key) && day < today.AddDays(-2)) continue;
            var n = MatchesBetween(start, day.AddDays(1).ToUniversalTime());
            if (n == null) return; // stats indisponibles : on réessaiera
            _data.MatchDays[key] = n.Value;
        }
        _matchesToday = MatchesBetween(start, DateTime.UtcNow);
        _matchesRefreshed = DateTime.UtcNow;
        Save(DataPath, _data);
    }

    /// <summary>Parties jouées entre le début et un instant (heure locale), interpolé dans la journée.</summary>
    private static double MatchesAt(DateTime local, DateTime start)
    {
        var startLocal = start.ToLocalTime();
        if (local <= startLocal) return 0;
        var day = local.Date;
        double Cum(DateTime d) => d < startLocal.Date ? 0
            : d == DateTime.Now.Date ? _matchesToday ?? Cum(d.AddDays(-1))
            : _data.MatchDays.TryGetValue(d.ToString("yyyy-MM-dd"), out var n) ? n : Cum(d.AddDays(-1));
        var before = Cum(day.AddDays(-1));
        var after = Math.Max(before, Cum(day));
        var dayStart = Max(day, startLocal);
        var dayEnd = day == DateTime.Now.Date ? DateTime.Now : day.AddDays(1);
        var span = (dayEnd - dayStart).TotalSeconds;
        var f = span <= 0 ? 1 : Math.Clamp((local - dayStart).TotalSeconds / span, 0, 1);
        return before + (after - before) * f;
    }

    /// <summary>Coffres par partie, mesurés jusqu'au plafond (ou jusqu'à l'estimation par les coffres rares, plus longue).</summary>
    private static Pace? ComputePace(Season s, DateTime start, DateTime capAt)
    {
        if (_pace != null && DateTime.UtcNow - _pace.At < TimeSpan.FromMinutes(10)) return _pace;
        var before = MatchesBetween(start, capAt);
        var now = MatchesBetween(start, DateTime.UtcNow);
        if (before is not > 0 || now == null) return _pace;
        var ratio = (double)Cap / before.Value;
        if (s.Estimate is int rareEst && s.EstimateAt is DateTime rareAt)
        {
            var atRare = MatchesBetween(start, rareAt);
            if (atRare > before) ratio = (double)rareEst / atRare.Value;
        }
        _pace = new Pace(DateTime.UtcNow, ratio, before.Value, Math.Max(now.Value, before.Value));
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
    private static readonly List<(DateTime At, string Text, ConsoleColor Color)> Log = new();

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
        var today = DateTime.Now.Date;
        for (var d = today.AddDays(-12); d <= today; d = d.AddDays(1))
        {
            sb.AppendLine($"{d:yyyy-MM-dd}  {MatchesBetween(d.ToUniversalTime(), d.AddDays(1).ToUniversalTime())?.ToString() ?? "erreur"}");
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

    public static int Run(string dataDir)
    {
        _folder = Path.Combine(dataDir, "ps5");
        Directory.CreateDirectory(_folder);
        using var instance = new Mutex(false, AppInfo.InstanceMutexFor(dataDir) + ".ps5");
        if (!instance.WaitOne(0))
        {
            ConsoleUi.Text(L.T("Le compteur PS5 est déjà ouvert : regarde dans la barre des tâches.",
                "The PS5 counter is already open: look in the taskbar.",
                "El contador PS5 ya está abierto: búscalo en la barra de tareas."), ConsoleColor.Yellow);
            Thread.Sleep(5000);
            return 0;
        }
        ConsoleUi.SetTitle("Chest Counter · PS5");
        ConsoleUi.EnsureSize(100, 40);
        ImportFromNodeTool();
        _data = Load<Ps5Data>(DataPath) ?? new Ps5Data();

        while (true)
        {
            if (Load<AuthFile>(AuthPath) == null && !LoginScreen())
            {
                return 0;
            }
            var result = MainScreen();
            if (result != Exit.Relogin)
            {
                return 0;
            }
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

    private static void Add(string text, ConsoleColor color)
    {
        lock (Log)
        {
            Log.Add((DateTime.Now, text, color));
            if (Log.Count > 50) Log.RemoveAt(0);
        }
    }

    // ---- Écran principal ----------------------------------------------------------------------------------------

    private enum Exit { Quit, Relogin }

    private static Exit MainScreen()
    {
        ConsoleUi.Screen("Chest Counter · PS5");
        Frames.Invalidate();
        Task<bool>? polling = null;
        _nextPoll = DateTime.MinValue;
        var lastDraw = DateTime.MinValue;
        while (true)
        {
            if (polling == null && DateTime.Now >= _nextPoll)
            {
                polling = Task.Run(Poll);
                lastDraw = DateTime.MinValue;
            }
            if (polling is { IsCompleted: true })
            {
                var ok = polling.Result;
                polling = null;
                WriteReport();
                if (ok && !_reportOpened)
                {
                    _reportOpened = true;
                    OpenUrl(ReportPath);
                }
                _nextPoll = DateTime.Now + PollEvery;
                lastDraw = DateTime.MinValue;
                if (!ok)
                {
                    Frames.Invalidate();
                    ConsoleUi.Screen("Chest Counter · PS5");
                    ConsoleUi.Text(L.T("La connexion Epic n'est plus valide : il faut te reconnecter.", "The Epic sign-in is no longer valid: sign in again.", "La conexión Epic ya no es válida: vuelve a conectarte."), ConsoleColor.Yellow);
                    Thread.Sleep(2000);
                    return Exit.Relogin;
                }
            }
            if (DateTime.Now - lastDraw > TimeSpan.FromSeconds(1))
            {
                Frames.Draw(BuildFrame(polling != null));
                lastDraw = DateTime.Now;
            }
            if (ConsoleUi.Interactive && Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (char.ToLowerInvariant(key.KeyChar))
                {
                    case 'r':
                        if (polling == null) _nextPoll = DateTime.MinValue;
                        break;
                    case 'p':
                        WriteReport();
                        OpenUrl(ReportPath);
                        break;
                    case 'o':
                        OpenUrl(_folder);
                        break;
                    case 'c':
                        if (polling == null) ChooseCounter();
                        lastDraw = DateTime.MinValue;
                        break;
                    case 'd':
                        Frames.Invalidate();
                        ConsoleUi.Screen("Chest Counter · PS5");
                        if (ConsoleUi.Confirm(L.T("Se déconnecter (la connexion est aussi révoquée chez Epic)", "Sign out (also revoked at Epic)", "Cerrar sesión (también se revoca en Epic)"),
                                L.T("Annuler", "Cancel", "Cancelar")))
                        {
                            polling?.Wait();
                            Logout();
                            return Exit.Relogin;
                        }
                        Frames.Invalidate();
                        lastDraw = DateTime.MinValue;
                        break;
                    case 'q':
                        return Exit.Quit;
                }
                if (key.Key == ConsoleKey.Escape) return Exit.Quit;
            }
            Thread.Sleep(100);
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
        ConsoleUi.Screen("Chest Counter · PS5");
    }

    private static Frame BuildFrame(bool syncing)
    {
        var f = new Frame();
        var gold = ConsoleColor.Yellow;
        var mut = ConsoleColor.DarkGray;
        var width = Math.Min(ConsoleUi.Width - 4, 90);

        var status = syncing ? (L.T("synchro…", "syncing…", "sincronizando…"), ConsoleColor.Cyan)
            : _error != null ? ("● " + L.T("erreur", "error", "error"), ConsoleColor.Red)
            : _lastSync != null ? ("● " + L.T("connecté", "connected", "conectado") + $" · {_lastSync:HH:mm:ss}", ConsoleColor.Green)
            : ("…", mut);
        f.Blank();
        f.Add(new Seg("  Chest Counter", gold), new Seg(" · PS5", ConsoleColor.Cyan), new Seg($"     {_player}   ", ConsoleColor.White), new Seg(status.Item1, status.Item2));
        f.Add("  " + new string('─', width), mut);
        f.Blank();

        var total = _tracked?.Value;
        var totalText = total == null ? "–" : L.N(total.Value) + (Capped ? "+" : "");
        foreach (var line in BigDigits.Render(totalText))
        {
            f.Add("    " + line, gold);
        }
        f.Add("    " + L.T("coffres ouverts cette saison", "chests opened this season", "cofres abiertos esta temporada"), mut);
        f.Blank();

        var todayKey = DateTime.Now.Date;
        var today = _data.History.Where(h => h.At.ToLocalTime().Date == todayKey).Sum(h => h.Delta);
        var session = total != null && _sessionStart != null ? total.Value - _sessionStart.Value : 0;
        var before = _data.Baseline?.Value;
        f.Add(
            new Seg("    " + L.T("Cette session ", "This session ", "Esta sesión "), mut), new Seg($"+{session}".PadRight(8), ConsoleColor.White),
            new Seg(L.T("Aujourd'hui ", "Today ", "Hoy "), mut), new Seg($"+{today}".PadRight(8), ConsoleColor.White),
            new Seg(L.T("Avant le suivi ", "Before tracking ", "Antes del seguimiento "), mut),
            new Seg(before == null ? "–" : L.N(before.Value) + (Capped ? "+" : ""), ConsoleColor.White),
            new Seg(_data.Baseline == null ? "" : $"  ({_data.Baseline.At.ToLocalTime():dd'/'MM})", mut));
        if (_season.Count.Count > 1)
        {
            f.Blank();
            f.Add("  " + L.T("TA SAISON", "YOUR SEASON", "TU TEMPORADA"), ConsoleColor.Cyan);
            f.Add("    " + string.Join("   ", _season.Count.Skip(1).Select(m => $"{L.N(m.Value)} ({m.At.ToLocalTime():dd'/'MM HH:mm})")), ConsoleColor.White);
            if (_season.PaceEstimate is int pace)
            {
                f.Add(new Seg("    ≈ " + L.N(pace), gold), new Seg(L.T($"  coffres estimés aujourd'hui (≈ {_season.PaceRatio:0} par partie × {_season.MatchesAfter} parties depuis les 1 000)",
                    $"  chests estimated today (≈ {_season.PaceRatio:0} per match × {_season.MatchesAfter} matches since 1,000)",
                    $"  cofres estimados hoy (≈ {_season.PaceRatio:0} por partida × {_season.MatchesAfter} partidas desde los 1000)"), mut));
            }
            if (_season.Estimate is int est)
            {
                f.Add(new Seg("    ≈ " + L.N(est), gold), new Seg(L.T($"  coffres estimés au {_season.EstimateAt!.Value.ToLocalTime():dd'/'MM} (d'après ton rythme de coffres rares)",
                    $"  chests estimated on {_season.EstimateAt!.Value.ToLocalTime():dd'/'MM} (from your rare chest pace)",
                    $"  cofres estimados el {_season.EstimateAt!.Value.ToLocalTime():dd'/'MM} (según tu ritmo de cofres raros)"), mut));
            }
        }
        if (Capped)
        {
            f.Blank();
            f.Add("    ⚠ " + L.T("La Chasse au trésor s'arrête à 1 000 chez Epic : au-delà, le total ne bouge plus.",
                "Epic's Treasure Hunt counter stops at 1,000: beyond that, the total no longer moves.",
                "La Caza del tesoro de Epic se detiene en 1000: después, el total ya no se mueve."), ConsoleColor.DarkYellow);
        }
        if (_error != null)
        {
            f.Blank();
            f.Add("    ✖ " + _error, ConsoleColor.Red);
        }

        // Par jour (14 derniers jours avec des gains)
        f.Blank();
        f.Add("  " + L.T("PAR JOUR", "PER DAY", "POR DÍA"), ConsoleColor.Cyan);
        var days = _season.PerDayExact.Keys.Union(_season.PerDayEstimated.Keys).OrderBy(d => d).TakeLast(7)
            .Select(d => (d, (int)Math.Round(_season.PerDayExact.GetValueOrDefault(d) + _season.PerDayEstimated.GetValueOrDefault(d)), _season.PerDayEstimated.GetValueOrDefault(d) >= 0.5))
            .Where(x => x.Item2 > 0).ToList();
        if (days.Count == 0)
        {
            f.Add("    " + L.T("Les jours apparaissent au fil des parties.", "Days appear as you play.", "Los días aparecen al jugar."), mut);
        }
        else
        {
            var max = Math.Max(1, days.Max(d => d.Item2));
            foreach (var (day, n, est) in days)
            {
                var bar = new string(est ? '▒' : '█', Math.Max(1, (int)Math.Round(30.0 * n / max)));
                f.Add(new Seg($"    {day:dd'/'MM}  ", mut), new Seg(bar, est ? ConsoleColor.DarkYellow : gold), new Seg((est ? " ≈ " : " ") + n, ConsoleColor.White));
            }
            if (days.Any(d => d.Item3)) f.Add("    " + L.T("▒ ≈ estimé (paliers de la quête + parties jouées)", "▒ ≈ estimated (quest stages + matches played)", "▒ ≈ estimado (etapas + partidas jugadas)"), mut);
        }

        // Derniers gains + activité
        f.Blank();
        f.Add("  " + L.T("DERNIERS GAINS", "LATEST GAINS", "ÚLTIMAS GANANCIAS"), ConsoleColor.Cyan);
        var recent = _data.History.TakeLast(5).Reverse().ToList();
        if (recent.Count == 0) f.Add("    " + L.T("Rien pour l'instant.", "Nothing yet.", "Nada por ahora."), mut);
        foreach (var h in recent)
        {
            f.Add(new Seg($"    {h.At.ToLocalTime():dd'/'MM HH:mm}  ", mut), new Seg($"+{h.Delta}", ConsoleColor.Green), new Seg($"  →  {L.N(h.Total)}", ConsoleColor.White));
        }

        // Compteurs Epic
        f.Blank();
        f.Add("  " + L.T("COMPTEURS EPIC", "EPIC COUNTERS", "CONTADORES EPIC"), ConsoleColor.Cyan);
        foreach (var c in _counters.Take(5))
        {
            var on = c.Id == _tracked?.Id;
            f.Add(new Seg(on ? "    ► " : "      ", gold), new Seg($"{c.Value,6}  ", on ? gold : ConsoleColor.White), new Seg(Pretty(c.Quest), mut));
        }

        List<(DateTime At, string Text, ConsoleColor Color)> log;
        lock (Log) log = Log.TakeLast(3).ToList();
        if (log.Count > 0)
        {
            f.Blank();
            foreach (var (at, text, color) in log) f.Add(new Seg($"    {at:HH:mm}  ", mut), new Seg(text, color));
        }

        f.Blank();
        var next = _nextPoll > DateTime.Now ? (int)(_nextPoll - DateTime.Now).TotalSeconds : 0;
        f.Add(
            new Seg("  [R] ", gold), new Seg(L.T("actualiser", "refresh", "actualizar") + (syncing ? "" : $" ({next}s)") + "   ", mut),
            new Seg("[P] ", gold), new Seg(L.T("page", "page", "página") + "   ", mut),
            new Seg("[C] ", gold), new Seg(L.T("compteur", "counter", "contador") + "   ", mut),
            new Seg("[O] ", gold), new Seg(L.T("dossier", "folder", "carpeta") + "   ", mut),
            new Seg("[D] ", gold), new Seg(L.T("déconnexion", "sign out", "cerrar sesión") + "   ", mut),
            new Seg("[Échap] ", gold), new Seg(L.T("quitter", "quit", "salir"), mut));
        return f;
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

/// <summary>Gros chiffres sur 3 lignes pour le total (style compteur).</summary>
public static class BigDigits
{
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { "█▀█", "█ █", "▀▀▀" },
        ['1'] = new[] { " ▀█", "  █", "  ▀" },
        ['2'] = new[] { "▀▀█", "█▀▀", "▀▀▀" },
        ['3'] = new[] { "▀▀█", " ▀█", "▀▀▀" },
        ['4'] = new[] { "█ █", "▀▀█", "  ▀" },
        ['5'] = new[] { "█▀▀", "▀▀█", "▀▀▀" },
        ['6'] = new[] { "█▀▀", "█▀█", "▀▀▀" },
        ['7'] = new[] { "▀▀█", "  █", "  ▀" },
        ['8'] = new[] { "█▀█", "█▀█", "▀▀▀" },
        ['9'] = new[] { "█▀█", "▀▀█", "▀▀▀" },
        ['+'] = new[] { "   ", "▄█▄", " ▀ " },
        ['–'] = new[] { "   ", "▀▀▀", "   " },
    };

    public static string[] Render(string text)
    {
        var lines = new[] { "", "", "" };
        foreach (var ch in text)
        {
            if (!Glyphs.TryGetValue(ch, out var g)) continue; // séparateurs de milliers ignorés
            for (var i = 0; i < 3; i++) lines[i] += g[i] + " ";
        }
        return lines;
    }
}
