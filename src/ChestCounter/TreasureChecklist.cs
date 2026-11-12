namespace ChestCounter;

/// <summary>
/// Écran « Trésors obtenus » : la liste des trésors de la semaine en cours, Entrée coche/décoche. Ce qui est coché
/// disparaît de l'onglet Coffres · chances de la barre. Rangé dans treasures.json (PC : dossier de données ; console : ps5\).
/// </summary>
public static class TreasureChecklist
{
    public static void Run(string folder, string appName)
    {
        if (!ConsoleUi.Interactive)
        {
            return; // sans vrai terminal, Menu renverrait toujours la même ligne : boucle sans fin
        }
        var store = new ObtainedStore(folder);
        var week = TreasureOdds.CurrentWeek(DateTime.UtcNow);
        var selected = 0;
        while (true)
        {
            ConsoleUi.Screen(appName, L.T("trésors obtenus", "treasures obtained", "tesoros conseguidos"));
            ConsoleUi.Text(L.T($"Trésors de la semaine {week.Number} : coche ce que tu as déjà eu.", $"Week {week.Number} treasures: tick what you already got.", $"Tesoros de la semana {week.Number}: marca lo que ya tienes."), ConsoleColor.White);
            ConsoleUi.Hint(L.T("Ce qui est coché n'apparaît plus dans l'onglet Coffres de la barre. Sur PC, un trésor trouvé dans un replay est coché tout seul.",
                "Ticked treasures no longer show in the bar's Chests tab. On PC, a treasure found in a replay is ticked by itself.",
                "Lo marcado ya no aparece en la pestaña Cofres de la barra. En PC, un tesoro encontrado en una repetición se marca solo."));
            ConsoleUi.Blank();
            var kinds = TreasureOdds.Kinds;
            var options = kinds.Select(k =>
            {
                var name = week.Prizes.FirstOrDefault(p => p.Kind == k.Kind)?.Name;
                return $"[{(store.Has(week.Number, k.Kind) ? "x" : " ")}] {TreasureOdds.KindLabel(k.Kind)}{(name != null ? "  ·  " + name : "")}";
            }).ToList();
            options.Add(L.T("Terminé", "Done", "Listo"));
            var done = options.Count - 1;
            var choice = ConsoleUi.Menu(options, selected, L.T("↑ ↓ choisir · Entrée cocher / décocher · Échap terminé", "↑ ↓ choose · Enter tick / untick · Esc done", "↑ ↓ elegir · Intro marcar / desmarcar · Esc listo"), escapeIndex: done);
            if (choice < 0 || choice >= done)
            {
                return;
            }
            var kind = kinds[choice].Kind;
            store.Set(week.Number, kind, !store.Has(week.Number, kind));
            selected = choice;
        }
    }
}
