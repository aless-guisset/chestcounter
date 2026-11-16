using System.Text.Json;

namespace ChestCounter;

/// <summary>Un type de trésor et sa cote par coffre (« 1 chance sur N »). Weekly = un trésor différent chaque semaine.</summary>
public sealed record TreasureKind(string Kind, int OneIn, bool Weekly);

/// <summary>Un trésor précis (nom connu), avec les identifiants envoyés par le coffre doré quand on l'obtient.</summary>
public sealed record Prize(string Id, string Kind, string? Name, string[] PrizeIds, string? Image = null);

/// <summary>Une semaine de la chasse au trésor (du dimanche 14:00 UTC au dimanche suivant).</summary>
public sealed record Week(int Number, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<Prize> Prizes);

/// <summary>
/// Cotes et trésors de la saison (chapitre 7 saison 3), tels que fortnite.bot les publie (noms, images). Source : le
/// content/content.json de LED-N (MIT), qui reprend fortnite.bot et ajoute les identifiants des coffres dorés.
/// Téléchargé au démarrage puis toutes les 2 h, gardé dans le dossier de données ; Assets/treasures.json en secours.
/// Une semaine absente a un numéro calculé et pas de noms.
/// </summary>
public static class TreasureOdds
{
    public const string Outfit = "outfit", PirateShop = "pirate-shop", Car = "car", Pickaxe = "pickaxe", Jam = "jam", Loading = "loading";

    /// <summary>Début de la semaine 1 : les semaines changent le dimanche à 14:00 UTC.</summary>
    public static readonly DateTime SeasonStartUtc = new(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>La page des trésors de la semaine sur fortnite.bot (images, cotes, heures du trésor).</summary>
    public const string SiteUrl = "https://fortnite.bot/treasure";

    private const string RemoteUrl = "https://raw.githubusercontent.com/LED-N/Fortnite-ChestCounter/main/content/content.json";
    private const string CacheFile = "treasures.cache.json";

    private sealed record Content(int Revision, List<TreasureKind> Kinds, Prize Pirate, List<Week> Weeks);

    private static Content? _data;
    private static Content Data => _data ??= Parse(ReadEmbedded()) ?? throw new InvalidOperationException("treasures.json invalide dans l'exe");

    /// <summary>Les 6 types affichés, dans l'ordre de la page Coffres (gadgets exclus : on peut les retrouver).</summary>
    public static IReadOnlyList<TreasureKind> Kinds => Data.Kinds;

    /// <summary>1 − (1 − 1/N)^coffres : la probabilité d'avoir obtenu au moins une fois ce trésor.</summary>
    public static double Chance(int oneIn, int chests) => chests <= 0 || oneIn <= 0 ? 0 : 1 - Math.Pow(1 - 1.0 / oneIn, chests);

    public static Week CurrentWeek(DateTime utcNow)
    {
        var number = (int)Math.Floor((utcNow - SeasonStartUtc).TotalDays / 7) + 1;
        if (Data.Weeks.FirstOrDefault(w => utcNow >= w.StartUtc && utcNow < w.EndUtc) is { } known)
        {
            return known;
        }
        var start = SeasonStartUtc.AddDays(7 * (number - 1));
        return new Week(number, start, start.AddDays(7), Array.Empty<Prize>());
    }

    /// <summary>Le trésor correspondant à un identifiant vu dans un replay (« DelilahPrize:dpid_item1 », GUID de la boutique pirate).</summary>
    public static Prize? FindByPrizeId(string prizeId)
    {
        if (Data.Pirate.PrizeIds.Contains(prizeId, StringComparer.OrdinalIgnoreCase)) return Data.Pirate;
        return Data.Weeks.SelectMany(w => w.Prizes).FirstOrDefault(p => p.PrizeIds.Contains(prizeId, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Numéro de la semaine où ce trésor était à gagner (null pour la boutique pirate ou un inconnu).</summary>
    public static int? WeekOf(Prize prize) => Data.Weeks.FirstOrDefault(w => w.Prizes.Any(p => p.Id == prize.Id))?.Number;

    public static string KindLabel(string kind) => kind switch
    {
        Outfit => L.T("Tenue en or", "Gold outfit", "Traje dorado"),
        PirateShop => L.T("Accès boutique pirate", "Pirate Shop Access", "Acceso a la tienda pirata"),
        Car => L.T("Sticker de voiture en or", "Gold car decal", "Calcomanía dorada de coche"),
        Pickaxe => L.T("Pioche ou sac en or", "Gold pickaxe or back bling", "Pico o mochila dorados"),
        Jam => L.T("Piste de jam", "Jam track", "Pista de improvisación"),
        Loading => L.T("Écran de chargement", "Loading screen", "Pantalla de carga"),
        _ => kind,
    };

    /// <summary>Les 6 types qu'on sait afficher (les gizmos de content.json sont ignorés : on peut les retrouver).</summary>
    private static readonly string[] Shown = { Outfit, PirateShop, Car, Pickaxe, Jam, Loading };

    private static string ReadEmbedded()
    {
        using var stream = typeof(TreasureOdds).Assembly.GetManifestResourceStream("treasures.json")
            ?? throw new InvalidOperationException("treasures.json manquant dans l'exe");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Lit un content.json (LED-N) ou notre treasures.json ; null s'il n'est pas valide.</summary>
    private static Content? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var revision = root.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            var kinds = root.GetProperty("odds").EnumerateArray()
                .Select(o => new TreasureKind(o.GetProperty("kind").GetString()!, o.GetProperty("oneIn").GetInt32(), o.GetProperty("weekly").GetBoolean()))
                .Where(k => Shown.Contains(k.Kind) && k.OneIn > 0)
                .OrderBy(k => Array.IndexOf(Shown, k.Kind))
                .ToList();
            var pirate = ReadPrize(root.GetProperty("pirateShop"));
            var weeks = root.GetProperty("weeks").EnumerateArray().Select(w => new Week(
                w.GetProperty("number").GetInt32(),
                w.GetProperty("start").GetDateTime().ToUniversalTime(),
                w.GetProperty("end").GetDateTime().ToUniversalTime(),
                w.GetProperty("prizes").EnumerateArray().Select(ReadPrize).ToList())).ToList();
            return kinds.Count == 0 || weeks.Count == 0 ? null : new Content(revision, kinds, pirate, weeks);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static Prize ReadPrize(JsonElement p)
    {
        var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        var image = p.TryGetProperty("image", out var i) ? i.GetString() : null;
        // Semaine pas encore révélée : content.json met « Week 4 Gold Outfit »… sans image. Pas de nom plutôt qu'un faux.
        if (image == null && name != null && name.StartsWith("Week ", StringComparison.OrdinalIgnoreCase))
        {
            name = null;
        }
        return new(
            p.GetProperty("id").GetString()!,
            p.GetProperty("kind").GetString()!,
            name,
            p.TryGetProperty("prizeIds", out var ids) ? ids.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
            image);
    }

    /// <summary>Utilise la copie du dossier de données si elle est plus récente que celle de l'exe.</summary>
    public static void LoadCached(string dataFolder)
    {
        try
        {
            var path = Path.Combine(dataFolder, CacheFile);
            if (File.Exists(path) && Parse(File.ReadAllText(path)) is { } cached && cached.Revision > Data.Revision)
            {
                _data = cached;
            }
        }
        catch (IOException)
        {
            // copie illisible : on garde celle de l'exe
        }
    }

    /// <summary>
    /// Télécharge les trésors à jour (quelques Ko). Une version valide et plus récente est gardée et utilisée tout de
    /// suite. Vrai si les données ont changé. Aucune erreur remontée : sans réseau, on garde ce qu'on a.
    /// </summary>
    public static async Task<bool> RefreshAsync(string dataFolder)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounterPlus/{AppInfo.VersionText}");
            var json = await http.GetStringAsync(RemoteUrl);
            if (json.Length > 512 * 1024 || Parse(json) is not { } fresh || fresh.Revision <= Data.Revision)
            {
                return false;
            }
            _data = fresh;
            Directory.CreateDirectory(dataFolder);
            await File.WriteAllTextAsync(Path.Combine(dataFolder, CacheFile), json);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Remplace les données (tests).</summary>
    internal static bool LoadForTests(string json)
    {
        if (Parse(json) is not { } c) return false;
        _data = c;
        return true;
    }

    internal static void ResetForTests() => _data = null;
}
