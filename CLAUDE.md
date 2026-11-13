# ChestCounter — PC + console

Copie de [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter) (MIT, v1.4.0) avec un mode
console, appelée **ChestCounter+** (`ChestCounterPlus.exe`, données `%APPDATA%\ChestCounterPlus`, verrous et
lancement auto préfixés `ChestCounterPlus` : cohabite avec LED-N ; au 1er lancement copie `%APPDATA%\ChestCounter`).
Utilisateur francophone : UI, messages et commentaires du code ajouté en français.

- Notre dépôt : `origin` = `aless-guisset/chestcounter` (privé). Remote `upstream` = LED-N : **ne jamais pousser
  dessus**. Branche de travail : `ps5`. LED-N a sorti une 1.5.0 (fenêtre WebView2, console supprimée) : pas fusionnée.
- Une seule app : plateforme **PC** (replays, code d'origine) ou **Console** (`Ps5Mode.cs`, compte Epic), choisie au
  premier lancement, modifiable dans Réglages. `--ps5` / `--pc` la forcent.
- Mises à jour LED-N coupées (`AppInfo.UpdatesEnabled = false`) : elles remplaceraient l'exe et effaceraient le mode console.

## Build et installation (depuis WSL)

`./package.sh` fabrique `Bureau\ChestCounterPlus\ChestCounterPlus.zip` à partager (exe + .bat + `packaging/LISEZMOI.txt`).
`./deploy.sh` fait tout (compile, ferme l'app si elle tourne, copie l'exe). À la main :

```
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
cp dist/ChestCounterPlus.exe /mnt/c/Users/adm_loc/Desktop/ChestCounterPlus/
```

SDK .NET 10 dans `~/.dotnet` (Windows n'a que le SDK 8).
Dossier Windows unique `Bureau\ChestCounterPlus` (lancé avec Fortnite) : exe, zip à partager, `Lancer ChestCounter+.bat`, `Test XP (40 min).bat`, `Test lecture ecran (30 min).bat`
(résultat copié dans `Tests\`), `Mes donnees.bat`, `LISEZMOI.txt`.
Données réelles : `%APPDATA%\ChestCounterPlus` (mode console dans `ps5\` : auth.json, data.json, report.html, overlay).

**Tests unitaires** : `~/.dotnet/dotnet test tests/ChestCounter.Tests`.
**Tests de l'app** : toujours avec `--data <dossier de test>` et un `config.json` qui contient `"OpenReportOnStart": false`
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

- **Piste abandonnée** : enregistrement du son (retiré).
- **XP (test du 13/11, Recharge)** : le profil athena (`stats.xp`, `level`…) ne bouge **pas pendant la partie**, tout
  arrive d'un coup à la sortie (+XP, quêtes du jour). Pas de direct par l'API, mais **fin de partie détectable** :
  `ConsoleMatches` (XP changée entre deux relevés = partie finie), puis le joueur donne ses coffres (téléphone /
  menu « Dernière partie »), sinon sa moyenne (≈). L'XP d'une partie mélange tout : impossible d'en déduire les coffres.
- Recharge (`blastberry`/`punchberry`) compte comme le reste (stats Epic, replays PC).

## Téléphone et lecture d'écran

- `PhoneServer` : page servie sur le réseau local (`TcpListener`, port 8723, clé secrète dans l'URL/QR, `config.PhoneToken`),
  menu « Téléphone (QR code) » → `telephone.html` (QR SVG, QRCoder). Commandes du téléphone mises en file et appliquées
  par `Ps5Mode.Tick` (jamais pendant un relevé). Page : `Assets/phone.html`.
- `ScreenOcr` : capture d'une fenêtre (BitBlt écran) + OCR Windows (`Windows.Media.Ocr`, d'où la cible
  `net10.0-windows10.0.19041.0`). `--ocr-test [min] [titre]` note le texte lu (coffres, XP) dans `ocr\ocr.txt` + PNG.
  Étape suivante : régler la détection d'après ces relevés (PC : Fortnite ; console : fenêtre OBS / stream).

Outils de diagnostic : `--ps5-quests` (attributs bruts des quêtes), `--ps5-stats` (parties par jour UTC) et
`--ps5-xp [minutes]` (relève le profil toutes les 30 s et note ce qui change, dans `ps5\xp.txt`), `--ocr-test`.
