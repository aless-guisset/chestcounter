# ChestCounter — PC + console

Copie de [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter) (MIT, v1.4.0) avec un mode
console. Utilisateur francophone : UI, messages et commentaires du code ajouté en français.

- Remote `upstream` = LED-N : **ne jamais pousser dessus**. Branche de travail : `ps5`.
- Une seule app : plateforme **PC** (replays, code d'origine) ou **Console** (`Ps5Mode.cs`, compte Epic), choisie au
  premier lancement, modifiable dans Réglages. `--ps5` / `--pc` la forcent.
- Mises à jour LED-N coupées (`AppInfo.UpdatesEnabled = false`) : elles remplaceraient l'exe et effaceraient le mode console.

## Build et installation (depuis WSL)

```
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
cp dist/ChestCounter.exe /mnt/c/Users/adm_loc/ChestCounterPC/
```

SDK .NET 10 dans `~/.dotnet` (Windows n'a que le SDK 8). Raccourci unique : `Bureau\ChestCounter.bat`.
Données réelles : `%APPDATA%\ChestCounter` (mode console dans `ps5\` : auth.json, data.json, report.html, overlay).

**Tests** : toujours avec `--data <dossier de test>` et un `config.json` qui contient `"OpenReportOnStart": false`
(sinon chaque test ouvre un onglet chez l'utilisateur). Supprimer le dossier de test ensuite (il contient une copie
de la connexion Epic). Ne jamais laisser de faux chiffres (étalonnage bidon) visibles.

## Ce qu'on sait d'Epic (ne pas re-chercher)

- **Replays console : inaccessibles** (restent dans la console). Replays serveur Epic : tournois seulement.
- **Seul compteur de coffres** : quêtes « Chasse au trésor » (`quest_sNN_treasurehunt_count_01..05`, paliers
  25/100/250/500/**1 000**), aussi `_rarity_` (coffres rares, max 100) et `_sprites_`. Plafonné à 1 000.
  Rien d'autre dans les profils (athena, common_core, collections, metadata, creative, campaign), pas d'accolades BR.
- **Dates des paliers** : palier N atteint ≈ `creation_time` du palier N+1, sinon `last_state_change_time`.
- **statsv2** (`statsproxy…/statsv2/account/{id}?startTime=&endTime=`) : `br_matchesplayed_*`. Les parties sont
  rangées **par jour UTC** et publiées le lendemain vers 1 h ; un jour ne compte que si la fenêtre le couvre en
  entier → demander chaque jour `[00:00Z, 24:00Z)`. Pas d'heure de partie, pas d'ID de partie.
- Au-delà de 1 000 : estimation = dernier point sûr + parties depuis × coffres/partie (mesurés jusqu'au plafond, ou
  via le palier coffres rares), étalonnage optionnel donné par le joueur (parties, coffres). Pas de suivi en direct.
- Les noms de quêtes changent peut-être à chaque saison : adapter `TierRe` / `ChestRe` si les paliers ne sont plus reconnus.

Outils de diagnostic : `--ps5-quests` (attributs bruts des quêtes) et `--ps5-stats` (parties par jour UTC), écrits dans `ps5\`.
