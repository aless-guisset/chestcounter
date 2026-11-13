# Barre des coffres (nouvel overlay) — description

Date : 2026-11-12 · Branche `ps5` · Maquette validée : `%USERPROFILE%\ChestCounterPC\Maquettes\barre-coffres.html`

## Pourquoi

Le petit overlay actuel affiche plusieurs lignes en permanence. Le joueur veut un overlay discret en jeu (juste le
total), qu'il peut dérouler pour voir le détail et ses chances d'avoir les trésors de la saison. Ça doit marcher sur
**PC et PS5** : un chiffre approximatif pendant la partie, confirmé à la fin quand c'est possible.

## Ce qui est décidé (par le joueur)

1. **Barre fermée** : une seule barre fine avec l'icône, le **total de coffres** et un badge d'état.
2. **Barre déroulée, onglet Résumé** : partie en cours, **session**, **aujourd'hui** (avec nombre de parties et moyenne,
   comme la maquette).
3. **Barre déroulée, onglet « Coffres · chances »** : pour chaque trésor, la probabilité de l'avoir eu avec les coffres
   ouverts (%, jauge, « 1 chance sur N par coffre »).
4. **Cocher ce qu'on a eu** dans le terminal (la console de ChestCounter) : ↑ ↓, Espace pour cocher, Entrée pour
   valider. Les trésors cochés disparaissent de l'onglet Coffres.
5. **Approximatif puis confirmé** :
   - PC : chiffre en direct pendant la partie (badge « ● EN DIRECT »), puis confirmé par le replay terminé (« ✓ CONFIRMÉ »).
   - PS5 : estimation pendant la partie (« ≈ », badge « ≈ ESTIMATION »), confirmée par le compte Epic quand c'est possible.
     Pas de replays sur PS5.
6. **Session** :
   - PC : de l'ouverture de Fortnite à sa fermeture.
   - PS5 : du démarrage de ChestCounter à son arrêt.
7. Le visuel de la maquette (fond sombre, bordure et chiffres dorés, badges bleu/vert) est validé tel quel.

## Ce qui reste incertain (et comment on le gère)

**Le direct sur PS5 n'existe pas encore.** Aujourd'hui, au-delà de 1 000 coffres, Epic ne donne rien pendant la partie,
et les parties ne sont publiées que le lendemain (voir `CLAUDE.md`). Le test `--ps5-xp` en cours dira si l'XP ou une
quête bouge pendant ou juste après la partie.
- **Si oui** : estimation en direct à partir de ce signal (par ex. XP gagnée liée aux coffres), puis confirmation quand le
  profil se met à jour.
- **Si non** : la barre PS5 affiche l'estimation actuelle de `Ps5Mode` (avec « ≈ »), sans chiffre de partie en cours
  (« Partie en cours » masquée), et le badge reste « ≈ ESTIMATION ». Pas de faux direct.

On construit d'abord tout ce qui ne dépend pas de ce test : la barre, les onglets, les chances, les cases à cocher,
les sessions, et le PC complet. Le direct PS5 est une étape séparée, après le résultat du test.

## Mes choix (à vérifier en lisant)

- **Dérouler en jeu : un seul raccourci, Ctrl+Maj+E**, qui fait le tour fermé → Résumé → Coffres → fermé. La souris
  n'est pas libre en jeu. Quand l'overlay est déverrouillé (Ctrl+Maj+L), un clic sur la barre fait pareil, et un clic
  sur un onglet l'ouvre. Ctrl+Maj+O (afficher/masquer) et Ctrl+Maj+L (verrouiller) restent.
- **Cotes et trésors** : copie **fixe** des cotes et des trésors par semaine du `content/content.json` de LED-N 1.5.0
  (MIT, auteur cité), dans l'exe. **Pas de téléchargement depuis son GitHub.** Conséquence : quand une semaine n'a pas de
  noms connus, on affiche le type (« Tenue en or de la semaine ») avec sa cote, sans nom. Mettre à jour = nouvelle version.
- **Calcul** : chance = 1 − (1 − 1/N)^coffres.
  - Trésors **de la semaine** (tenue, sticker de voiture, pioche/sac, jam, écran de chargement) : seulement les coffres
    ouverts **pendant la semaine en cours** (les semaines changent le dimanche à 14 h UTC).
  - **Accès boutique pirate** : tous les coffres de la saison.
  - Les gadgets (« gizmo », retrouvables plusieurs fois) ne sont pas listés.
  - PS5 : coffres de la semaine = estimation (parties de la semaine × coffres/partie, ou différence de compteur sous
    1 000). Marqués « ≈ ».
- **Heures du trésor (cotes boostées)** : hors de cette version. Les cotes affichées sont les cotes normales.
- **Ce qu'on a coché** est rangé par semaine dans le dossier de données (`treasures.json`, un fichier pour PC, un dans
  `ps5\`), et conservé d'une semaine à l'autre (l'accès boutique pirate reste coché toute la saison).
- **Session PC quand Fortnite est fermé** : la barre garde la dernière session, libellée « dernière session », jusqu'au
  prochain lancement du jeu.
- **Accès aux cases à cocher** : une touche **T** (« Trésors obtenus ») dans l'écran principal de la console.
- **Aujourd'hui** = jour local (minuit heure de Belgique), pas UTC. Sur PS5, « aujourd'hui » reste une estimation.
- **Overlay OBS** : suit le même modèle si c'est simple, sinon inchangé.
- Le nouvel overlay **remplace** l'actuel (style « classique » de LED-N gardé dans les réglages s'il ne gêne pas).

## Non-objectifs

- Fusionner la 1.5.0 de LED-N (fenêtre WebView2). On reste sur notre base console.
- Détection par le son (abandonnée) ou via les rediffusions PS5 (inaccessibles).
- Heures du trésor, collection avec images, trésors des autres joueurs.

## Échecs à éviter (même si « ça marche »)

- Un chiffre PS5 présenté comme sûr alors que c'est une estimation (toujours « ≈ » ou badge bleu).
- Une barre qui prend de la place ou vole le focus du jeu (la fenêtre reste `NOACTIVATE`, pas de clic capturé quand elle est verrouillée).
- Des pourcentages faux parce qu'on mélange les semaines.
- Des données d'essai visibles chez le joueur (tests avec `--data` et `OpenReportOnStart:false`).

## Laissé au développeur

Dessin exact en GDI dans `NativeOverlay.cs` (tailles, marges), découpage des fichiers (par ex. `TreasureOdds.cs`,
`Sessions.cs`), textes FR/EN/ES dans le style de `L.T(...)`, tests unitaires du calcul de chances et des bornes de
semaine/session.
