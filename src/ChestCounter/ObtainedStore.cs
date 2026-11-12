using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// Les trésors que le joueur a déjà eus (cochés dans la console, ou vus dans un replay PC), dans treasures.json du dossier
/// de données (PC : le dossier normal ; console : ps5\). Rangés par semaine : « 2 » → ["outfit", "loading"].
/// L'accès boutique pirate vaut pour toute la saison.
/// </summary>
public sealed class ObtainedStore
{
    private readonly string _path;
    private readonly Dictionary<int, HashSet<string>> _weeks;

    public ObtainedStore(string folder)
    {
        _path = Path.Combine(folder, "treasures.json");
        _weeks = new();
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path)) is { } saved)
            {
                foreach (var (week, kinds) in saved)
                {
                    if (int.TryParse(week, out var n)) _weeks[n] = new HashSet<string>(kinds);
                }
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write("treasures.json", e); // fichier abîmé : on repart de rien plutôt que de planter
        }
    }

    public bool Has(int week, string kind) => kind == TreasureOdds.PirateShop
        ? _weeks.Values.Any(k => k.Contains(kind))
        : _weeks.TryGetValue(week, out var kinds) && kinds.Contains(kind);

    public void Set(int week, string kind, bool obtained)
    {
        if (!obtained && kind == TreasureOdds.PirateShop)
        {
            foreach (var k in _weeks.Values) k.Remove(kind); // décocher la boutique pirate la décoche pour toute la saison
        }
        else if (obtained)
        {
            if (!_weeks.TryGetValue(week, out var kinds)) _weeks[week] = kinds = new HashSet<string>();
            kinds.Add(kind);
        }
        else if (_weeks.TryGetValue(week, out var kinds))
        {
            kinds.Remove(kind);
        }
        Save();
    }

    /// <summary>Coche les trésors reconnus (identifiant du coffre doré) ; retourne combien ont été ajoutés.</summary>
    public int AutoTick(IEnumerable<TreasureFind> finds, DateTime matchStartUtc)
    {
        var added = 0;
        foreach (var f in finds)
        {
            if (f.PrizeId == null || TreasureOdds.FindByPrizeId(f.PrizeId) is not { } prize) continue;
            var week = TreasureOdds.WeekOf(prize) ?? TreasureOdds.CurrentWeek(matchStartUtc).Number;
            if (Has(week, prize.Kind)) continue;
            Set(week, prize.Kind, true);
            added++;
        }
        return added;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = _weeks.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.OrderBy(k => k).ToArray());
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
