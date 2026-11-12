# Barre des coffres — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remplacer l'overlay par une barre déroulante (total → Résumé → Coffres·chances), PC et PS5, avec cases « trésors obtenus » dans la console, et une version installable à côté du ChestCounter de LED-N.

**Architecture:** Logique pure testable (cotes, semaines, chances, sessions, modèle de barre) dans de nouveaux fichiers sans dépendance Windows ; `NativeOverlay.cs` ne fait que dessiner un `BarView`. PC et PS5 construisent chacun un `BarView` depuis leurs données.

**Tech Stack:** C# .NET 10, Win32/GDI (P/Invoke existant), xUnit pour les tests (nouveau projet `tests/ChestCounter.Tests`).

**Spec:** `docs/superpowers/specs/2026-11-12-barre-coffres-design.md`

## Global Constraints

- Textes en trois langues via `L.T(fr, en, es)` ; commentaires du code ajouté en français.
- Badges exacts : PC direct `● EN DIRECT`, PC fin `✓ CONFIRMÉ (replay)`, PS5 `≈ ESTIMATION`, PS5 confirmé `✓ CONFIRMÉ (Epic)`. Tout chiffre PS5 estimé est préfixé `≈ `.
- Chance = 1 − (1 − 1/N)^coffres. Semaines : changent le dimanche 14:00 UTC (début semaine 1 : 2026-11-01T14:00:00Z). Accès boutique pirate : coffres de toute la saison. Gizmos non listés.
- Cotes (1 sur N) : tenue en or 1 562 500 (semaine), accès boutique pirate 137 500 (saison), sticker voiture 7 813, pioche/sac 568, jam 391, écran de chargement 272 (semaine).
- Pas de réseau ajouté (cotes en copie fixe, crédit LED-N/MIT dans le fichier).
- Session PC = depuis le démarrage du processus `FortniteClient-Win64-Shipping` ; Fortnite fermé → « dernière session » gardée. Session PS5 = depuis le démarrage de ChestCounter. « Aujourd'hui » = minuit heure locale.
- Raccourci Ctrl+Maj+E : fermé → Résumé → Coffres → fermé. Ctrl+Maj+O et Ctrl+Maj+L inchangés. Fenêtre toujours `WS_EX_NOACTIVATE`.
- Tests manuels uniquement avec `--data <dossier test>` + `config.json` `"OpenReportOnStart": false` ; dossier supprimé après.
- Identité séparée de LED-N (Task 1) : jamais lire/écrire `%APPDATA%\ChestCounter` après l'import par copie.

## Review Focus

1. Changement de semaine en pleine session (dimanche 14:00 UTC) : les trésors « de la semaine » repartent de 0 coffre, l'accès boutique pirate non → test `Chances_ResetAtWeekBoundary` (Task 2).
2. 0 coffre / données vides / première utilisation : barre « 0 », chances 0 %, pas d'exception → tests `Build_EmptyData` (Task 5) et `Chance_ZeroChests` (Task 2).
3. Tous les trésors cochés : onglet Coffres affiche une ligne « Tout obtenu cette semaine » au lieu d'un vide → test `Build_AllObtained` (Task 5).
4. Ami avec LED-N 1.5 déjà installé : import par copie sans toucher l'original, records d'analyse > 15 forcés en réanalyse → test `Import_CopiesAndLowersAnalyzerVersion` (Task 1).
5. Fortnite lancé avant ChestCounter (PC) : les parties terminées depuis le lancement du jeu comptent dans la session → test `PcSession_IncludesMatchesSinceGameStart` (Task 4).

---

### Task 1: Identité séparée de LED-N + projet de tests

**Files:**
- Create: `tests/ChestCounter.Tests/ChestCounter.Tests.csproj` (xUnit, `net10.0`, ProjectReference vers `src/ChestCounter/ChestCounter.csproj`), `tests/ChestCounter.Tests/ImportTests.cs`
- Modify: `src/ChestCounter/AppInfo.cs` (nom, mutex), `src/ChestCounter/Models.cs:154-160` (`DefaultFolder`), `src/ChestCounter/FortniteWatch.cs:17` (`RunValue`), `src/ChestCounter/ChestCounter.csproj` (`AssemblyName` = `ChestCounterPlus`), `deploy.sh`, `CLAUDE.md`

**Interfaces:**
- Produces: `AppInfo.DataFolderName = "ChestCounterPlus"`, `AppInfo.DisplayName = "ChestCounter+"`; `Store.ImportFromLedN(string fromFolder, string toFolder) -> bool` (copie si `toFolder` absent ; ignore `webview\`, `images\` ; dans le `data.json` copié, tout `AnalyzerVersion` > `ReplayAnalyzer.CurrentVersion` devient 0, au niveau racine et par match) ; `DefaultFolder()` appelle l'import depuis `%APPDATA%\ChestCounter` une fois.
- Mutex et valeur Run préfixés `ChestCounterPlus` (plus de collision avec LED-N).

- [ ] **Step 1:** Créer le projet de tests ; `~/.dotnet/dotnet test tests/ChestCounter.Tests` passe (0 test).
- [ ] **Step 2: Test** `Import_CopiesAndLowersAnalyzerVersion` : dossier source temporaire avec `config.json`, `data.json` (`AnalyzerVersion` 16, un match à 16, un à 12), `ps5/auth.json`, `webview/x` → après import : destination contient `config.json`, `ps5/auth.json`, pas `webview` ; versions 16 → 0, 12 reste 12 ; source inchangée (même octets). Second appel → `false`, rien réécrit.
- [ ] **Step 3:** Lancer, voir l'échec.
- [ ] **Step 4:** Implémenter ; renommer assembly/mutex/Run/dossier ; `deploy.sh` copie `ChestCounterPlus.exe` dans `C:\Users\adm_loc\ChestCounterPC\` et met à jour les `.bat` + `Bureau\ChestCounter.bat`.
- [ ] **Step 5:** Tests verts ; build OK.
- [ ] **Step 6:** Commit `feat: identité séparée de LED-N (dossier ChestCounterPlus, import par copie)`.

### Task 2: Cotes, semaines et calcul des chances

**Files:**
- Create: `src/ChestCounter/TreasureOdds.cs`, `src/ChestCounter/Assets/treasures.json` (copie des `odds`, `pirateShop`, `weeks` de `content/content.json` de LED-N 1.5.0 : `git show upstream/main:content/content.json`, sans `treasureHours`/`notices` ; en tête `"source": "LED-N/Fortnite-ChestCounter 1.5.0 (MIT)"`), `tests/ChestCounter.Tests/TreasureOddsTests.cs`
- Modify: `src/ChestCounter/ChestCounter.csproj` (EmbeddedResource `treasures.json`)

**Interfaces:**
- Produces:
  - `record TreasureKind(string Kind, int OneIn, bool Weekly)` ; kinds `outfit, pirate-shop, car, pickaxe, jam, loading`.
  - `record Prize(string Id, string Kind, string? Name, string[] PrizeIds)` ; `record Week(int Number, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<Prize> Prizes)`.
  - `static class TreasureOdds` : `IReadOnlyList<TreasureKind> Kinds` ; `Week CurrentWeek(DateTime utcNow)` (semaine hors liste → numéro calculé depuis 2026-11-01T14:00Z, `Prizes` vides) ; `double Chance(int oneIn, int chests)` ; `Prize? FindByPrizeId(string prizeId)`.
  - `string KindLabel(string kind)` : « Tenue en or », « Accès boutique pirate », « Sticker de voiture en or », « Pioche ou sac en or », « Piste de jam », « Écran de chargement » (FR/EN/ES).

- [ ] **Step 1: Tests** : `Chance_ZeroChests` (= 0) ; `Chance_Known` (`Chance(272, 272)` ≈ 0.6328 ± 0.0005) ; `CurrentWeek_Boundary` (2026-11-08T13:59:59Z → 1 ; 2026-11-08T14:00:00Z → 2) ; `CurrentWeek_Unknown` (2026-12-20 → numéro 7, `Prizes` vide) ; `FindByPrizeId("DelilahPrize:dpid_item1")` → `w1-outfit` ; `Kinds` contient 6 entrées et pas `gizmo`.
- [ ] **Step 2:** Échec. **Step 3:** Implémenter (lecture de la ressource à la première utilisation). **Step 4:** Verts.
- [ ] **Step 5:** Commit `feat: cotes des trésors et calcul des chances (copie LED-N 1.5.0, MIT)`.

### Task 3: Trésors obtenus (stockage + coche auto PC)

**Files:**
- Create: `src/ChestCounter/ObtainedStore.cs`, `tests/ChestCounter.Tests/ObtainedStoreTests.cs`

**Interfaces:**
- Consumes: `TreasureOdds.CurrentWeek`, `FindByPrizeId`.
- Produces: `sealed class ObtainedStore(string folder)` → fichier `<folder>/treasures.json` ; `bool Has(int week, string kind)` (pour `pirate-shop` : vrai si coché n'importe quelle semaine) ; `void Set(int week, string kind, bool obtained)` (écriture atomique) ; `int AutoTick(IEnumerable<TreasureFind> finds, DateTime matchStartUtc)` (coche les `PrizeId` reconnus dans la semaine du match, retourne le nombre ajouté).

- [ ] **Step 1: Tests** : `SetHas_RoundTrip` (nouvelle instance relit) ; `PirateShop_IsSeasonWide` (coché semaine 1 → `Has(3, "pirate-shop")` vrai) ; `Weekly_IsPerWeek` (outfit coché sem. 1 → faux sem. 2) ; `AutoTick_FromPrizeId` (find `DelilahPrize:dpid_item1` début 2026-11-03 → `Has(1,"outfit")`) ; `MissingFile_Empty`.
- [ ] **Step 2–4:** Échec, implémenter, verts.
- [ ] **Step 5:** Commit `feat: trésors obtenus (treasures.json, coche auto depuis les replays PC)`.

### Task 4: Sessions et « aujourd'hui »

**Files:**
- Create: `src/ChestCounter/Sessions.cs`, `tests/ChestCounter.Tests/SessionsTests.cs`
- Modify: `src/ChestCounter/FortniteWatch.cs` (ajouter `DateTime? GameStartUtc()` = plus ancien `StartTime` des processus du jeu, en UTC, null si absent)

**Interfaces:**
- Produces: `static class Sessions` : `DateTime? PcSessionStart(DateTime? gameStartUtc, DateTime? lastKnownStartUtc)` (jeu lancé → son début ; fermé → dernier connu) ; `bool InWindow(MatchRecord m, DateTime startUtc)` (fin de partie = `MatchStartUtc + DurationSeconds`, sinon `AnalyzedAtUtc`, ≥ start) ; `DateTime TodayStartUtc(DateTime localNow)` (minuit local converti en UTC). La dernière heure de lancement connue est gardée dans `Config.LastGameStartUtc` (nouvelle propriété).

- [ ] **Step 1: Tests** : `PcSession_IncludesMatchesSinceGameStart` (jeu lancé 20:00Z, match fini 20:30Z avant démarrage du compteur à 20:45Z → dans la session) ; `PcSession_GameClosed_KeepsLast` ; `PcSession_NeverStarted_Null` ; `TodayStart_Local` (Europe/Brussels, 2026-11-12 00:30 local → 2026-11-11T23:00Z).
- [ ] **Step 2–4:** Échec, implémenter, verts.
- [ ] **Step 5:** Commit `feat: sessions (PC = lancement de Fortnite) et début de journée local`.

### Task 5: Modèle de la barre (PC et PS5)

**Files:**
- Create: `src/ChestCounter/BarModel.cs`, `tests/ChestCounter.Tests/BarModelTests.cs`
- Modify: `src/ChestCounter/OverlayModel.cs` (garde l'ancien `Build` pour le style classique et l'OBS)

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:
  - `enum BarState { Live, Confirmed, Estimate, EstimateConfirmed }`
  - `record BarLine(string Label, string Value, string? Detail)` ; `record ChanceLine(string Label, string Percent, double Fill, string Odds)`
  - `record BarView(string Total, BarState State, string Badge, IReadOnlyList<BarLine> Summary, IReadOnlyList<ChanceLine> Chances, string? ChancesEmpty)`
  - `BarModel.ForPc(TrackerData d, MatchRecord? live, bool inProgress, DateTime? sessionStartUtc, bool sessionRunning, ObtainedStore obtained, DateTime utcNow) -> BarView` : Summary = « Partie en cours » (seulement si `inProgress`), « Session » ou « Dernière session » (détail « n parties · moy. x »), « Aujourd'hui » (même détail) ; State `Live` si `inProgress`, sinon `Confirmed`. Coffres de la semaine = matches dont la fin est dans `CurrentWeek` + live ; saison = tous.
  - `BarModel.ForPs5(int? total, bool totalEstimated, int? today, bool todayEstimated, int session, bool sessionKnown, int weekChests, bool weekEstimated, int seasonChests, ObtainedStore obtained, DateTime utcNow) -> BarView` : State `Estimate` si un chiffre affiché est estimé, sinon `EstimateConfirmed` ; pas de ligne « Partie en cours » ; « — » quand inconnu ; préfixe `≈ ` sur les estimés.
  - Pourcentage formaté : ≥ 10 → « 63 % », ≥ 1 → « 4,2 % », sinon 3 décimales « 0,092 % » (culture `L.Culture`) ; `Fill` = min(1, p).
- [ ] **Step 1: Tests** : `Build_EmptyData` (Total « 0 », 6 chances à « 0,000 % ») ; `Build_PcLive` (badge `● EN DIRECT`, ligne « Partie en cours ») ; `Build_PcConfirmed` (`✓ CONFIRMÉ (replay)`) ; `Build_Ps5Estimate` (Total commence par « ≈ », badge `≈ ESTIMATION`, pas de « Partie en cours ») ; `Build_AllObtained` (tout coché → `Chances` vide, `ChancesEmpty` = « Tout obtenu cette semaine ») ; `Chances_ResetAtWeekBoundary` (100 coffres sem. 1, 0 sem. 2, now sem. 2 → outfit 0 %, boutique pirate > 0).
- [ ] **Step 2–4:** Échec, implémenter, verts.
- [ ] **Step 5:** Commit `feat: modèle de la barre des coffres (PC et PS5)`.

### Task 6: Dessin de la barre dans l'overlay

**Files:**
- Modify: `src/ChestCounter/NativeOverlay.cs` (nouveau style par défaut `bar` ; `Update(BarView)` ; état `Collapsed/Summary/Chances` ; hotkey id 3 Ctrl+Maj+E ; clic gauche sans glisser quand déverrouillé : sur la barre = cycle, sur un onglet = cet onglet), `src/ChestCounter/Program.cs:1470-1480` (`UpdateOverlay` PC → `BarModel.ForPc`), `src/ChestCounter/Ps5Mode.cs:1107-1133` (`UpdateOverlay` → `BarModel.ForPs5`), `src/ChestCounter/Program.cs` aide (ligne des raccourcis : ajouter Ctrl+Maj+E)

**Interfaces:**
- Consumes: `BarView` (Task 5). Couleurs de la maquette : fond `#0B0F18` (opacité réglage), bordure `#FFD666` à 35 %, total `#FFD666`, libellés `#B8BFCC`, détails `#7D8597`, badge direct/estimation bleu `#9CC0FF`, confirmé vert `#8DE0A5`. Largeur 270 px à 100 %, mise à l'échelle avec `Sc()`.

- [ ] **Step 1:** Implémenter le dessin et le cycle.
- [ ] **Step 2:** Build, puis captures sur Windows avec `CHESTCOUNTER_OVERLAY_SNAPSHOT` dans un dossier `--data` de test (fermé, Résumé, Coffres ; PC et PS5) ; comparer à la maquette `C:\Users\adm_loc\ChestCounterPC\Maquettes\barre-coffres.html`. Attendu : barre fermée = icône + total + badge ; aucun chevauchement de texte à 70 % et 175 %.
- [ ] **Step 3:** Commit `feat: overlay en barre déroulante (Ctrl+Maj+E)`.

### Task 7: « Trésors obtenus » dans la console

**Files:**
- Modify: `src/ChestCounter/Program.cs:754+` (`Items()` onglet 0 : entrée « Trésors obtenus (cocher) », PC et Console), nouvel écran via `SubScreen` : liste des 6 types de la semaine en cours (nom du trésor si connu, sinon `KindLabel`), ↑ ↓, Espace coche/décoche, Entrée/Échap valide ; écrit dans l'`ObtainedStore` du mode (PC : dossier de données ; PS5 : `ps5\`) puis met l'overlay à jour.

- [ ] **Step 1:** Implémenter.
- [ ] **Step 2:** Vérif manuelle dans un dossier de test : cocher « Écran de chargement » → `treasures.json` le contient, capture de l'onglet Coffres sans cette ligne.
- [ ] **Step 3:** Commit `feat: écran Trésors obtenus dans la console`.

### Task 8: Paquet à partager

**Files:**
- Create: `package.sh` (build, puis dossier `dist/ChestCounterPlus/` avec `ChestCounterPlus.exe`, `Lancer ChestCounter+.bat`, `Test XP (40 min).bat` adapté, `LISEZMOI.txt` (installation à côté de LED-N), zip `C:\Users\adm_loc\Desktop\ChestCounterPlus.zip` via PowerShell `Compress-Archive`)
- Modify: `CLAUDE.md` (nouveau nom, import, paquet)

- [ ] **Step 1:** Écrire le script, le lancer ; le zip contient exactement ces 4 fichiers, pas de `auth.json`.
- [ ] **Step 2:** Test d'installation propre : dézipper dans un dossier temporaire Windows, lancer avec `--data <test>` → démarre, barre affichée ; supprimer le dossier.
- [ ] **Step 3:** Commit `chore: paquet ChestCounterPlus.zip à partager`, push sur `origin` (vérifier `git remote -v` avant).
