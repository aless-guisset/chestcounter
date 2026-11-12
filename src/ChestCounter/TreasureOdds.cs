using System.Text.Json;

namespace ChestCounter;

/// <summary>Un type de trésor et sa cote par coffre (« 1 chance sur N »). Weekly = un trésor différent chaque semaine.</summary>
public sealed record TreasureKind(string Kind, int OneIn, bool Weekly);

/// <summary>Un trésor précis (nom connu), avec les identifiants envoyés par le coffre doré quand on l'obtient.</summary>
public sealed record Prize(string Id, string Kind, string? Name, string[] PrizeIds);

/// <summary>Une semaine de la chasse au trésor (du dimanche 14:00 UTC au dimanche suivant).</summary>
public sealed record Week(int Number, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<Prize> Prizes);

/// <summary>
/// Cotes et trésors de la saison (chapitre 7 saison 3), copie fixe de content/content.json de LED-N 1.5.0 (MIT) dans
/// Assets/treasures.json. Pas de téléchargement : une semaine absente du fichier a un numéro calculé et pas de noms.
/// </summary>
public static class TreasureOdds
{
    public const string Outfit = "outfit", PirateShop = "pirate-shop", Car = "car", Pickaxe = "pickaxe", Jam = "jam", Loading = "loading";

    /// <summary>Début de la semaine 1 : les semaines changent le dimanche à 14:00 UTC.</summary>
    public static readonly DateTime SeasonStartUtc = new(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc);

    private static readonly Lazy<(List<TreasureKind> Kinds, Prize Pirate, List<Week> Weeks)> Data = new(Load);

    /// <summary>Les 6 types affichés, dans l'ordre de la page Coffres (gadgets exclus : on peut les retrouver).</summary>
    public static IReadOnlyList<TreasureKind> Kinds => Data.Value.Kinds;

    /// <summary>1 − (1 − 1/N)^coffres : la probabilité d'avoir obtenu au moins une fois ce trésor.</summary>
    public static double Chance(int oneIn, int chests) => chests <= 0 || oneIn <= 0 ? 0 : 1 - Math.Pow(1 - 1.0 / oneIn, chests);

    public static Week CurrentWeek(DateTime utcNow)
    {
        var number = (int)Math.Floor((utcNow - SeasonStartUtc).TotalDays / 7) + 1;
        if (Data.Value.Weeks.FirstOrDefault(w => utcNow >= w.StartUtc && utcNow < w.EndUtc) is { } known)
        {
            return known;
        }
        var start = SeasonStartUtc.AddDays(7 * (number - 1));
        return new Week(number, start, start.AddDays(7), Array.Empty<Prize>());
    }

    /// <summary>Le trésor correspondant à un identifiant vu dans un replay (« DelilahPrize:dpid_item1 », GUID de la boutique pirate).</summary>
    public static Prize? FindByPrizeId(string prizeId)
    {
        if (Data.Value.Pirate.PrizeIds.Contains(prizeId, StringComparer.OrdinalIgnoreCase)) return Data.Value.Pirate;
        return Data.Value.Weeks.SelectMany(w => w.Prizes).FirstOrDefault(p => p.PrizeIds.Contains(prizeId, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Numéro de la semaine où ce trésor était à gagner (null pour la boutique pirate ou un inconnu).</summary>
    public static int? WeekOf(Prize prize) => Data.Value.Weeks.FirstOrDefault(w => w.Prizes.Any(p => p.Id == prize.Id))?.Number;

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

    private static (List<TreasureKind>, Prize, List<Week>) Load()
    {
        using var stream = typeof(TreasureOdds).Assembly.GetManifestResourceStream("treasures.json")
            ?? throw new InvalidOperationException("treasures.json manquant dans l'exe");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var kinds = root.GetProperty("odds").EnumerateArray()
            .Select(o => new TreasureKind(o.GetProperty("kind").GetString()!, o.GetProperty("oneIn").GetInt32(), o.GetProperty("weekly").GetBoolean()))
            .ToList();
        var pirate = ReadPrize(root.GetProperty("pirateShop"));
        var weeks = root.GetProperty("weeks").EnumerateArray().Select(w => new Week(
            w.GetProperty("number").GetInt32(),
            w.GetProperty("start").GetDateTime().ToUniversalTime(),
            w.GetProperty("end").GetDateTime().ToUniversalTime(),
            w.GetProperty("prizes").EnumerateArray().Select(ReadPrize).ToList())).ToList();
        return (kinds, pirate, weeks);
    }

    private static Prize ReadPrize(JsonElement p) => new(
        p.GetProperty("id").GetString()!,
        p.GetProperty("kind").GetString()!,
        p.TryGetProperty("name", out var n) ? n.GetString() : null,
        p.TryGetProperty("prizeIds", out var ids) ? ids.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>());
}
