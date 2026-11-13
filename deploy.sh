#!/usr/bin/env bash
# Compile ChestCounter+ et l'installe dans le dossier privé Bureau\ChestCounterPlus-Prive (ferme l'app, relance l'attente
# de Fortnite) avec les outils de test, et met à jour la version publique Bureau\ChestCounterPlus.
set -euo pipefail
cd "$(dirname "$0")"
# Dossier utilisateur Windows (C:\Users\<toi>), trouvé à l'exécution.
WIN_HOME=$(/mnt/c/Windows/System32/cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')
WIN_HOME_U=$(wslpath -u "$WIN_HOME")
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "Get-Process ChestCounter,ChestCounterPlus -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3" || true
DEST="$WIN_HOME\\Desktop\\ChestCounterPlus-Prive"
D=$(wslpath -u "$DEST")
cp dist/ChestCounterPlus.exe "$D/"
crlf() { sed 's/\r*$/\r/' ; }
crlf > "$D/Test lecture ecran (30 min).bat" <<'BAT'
@echo off
cd /d "%~dp0"
echo Lecture de l'ecran pendant 30 min : note le texte affiche quand tu ouvres un coffre.
echo Screen reader for 30 min: logs the text shown when you open a chest.
echo.
echo PC : laisse vide / leave empty (Fortnite in "Windowed Fullscreen").
echo Console : un mot du titre de la fenetre du jeu / a word of the game window title (OBS, Twitch, YouTube).
echo.
set "WIN="
set /p WIN=Fenetre / Window (Entree / Enter = Fortnite) : 
if "%WIN%"=="" set "WIN=Fortnite"
echo.
echo Lecture en cours (30 min) de la fenetre "%WIN%"... Ferme cette fenetre pour arreter.
echo Reading window "%WIN%" (30 min)... Close this window to stop.
start "" /wait /b "%~dp0ChestCounterPlus.exe" --ocr-test 30 "%WIN%"
echo Termine (code %errorlevel%) / Done.
start "" explorer "%APPDATA%\ChestCounterPlus\ocr"
pause
BAT
crlf > "$D/Test XP (40 min).bat" <<'BAT'
@echo off
cd /d "%~dp0"
echo Releve XP et quetes du compte Epic toutes les 30 s pendant 40 min.
echo.
start "" /wait /b ChestCounterPlus.exe --ps5-xp 40
if not exist "Tests" mkdir "Tests"
copy /y "%APPDATA%\ChestCounterPlus\ps5\xp.txt" "Tests\xp.txt" >nul && start "" notepad "%~dp0Tests\xp.txt"
pause
BAT
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "Start-Process -FilePath '$DEST\ChestCounterPlus.exe' -ArgumentList '--standby','--data','\"$WIN_HOME\AppData\Roaming\ChestCounterPlus\"'" || true
echo "Installé dans $DEST (attente de Fortnite relancée)"
# Même version dans le dossier public (sans les outils de test).
PUB="$WIN_HOME_U/Desktop/ChestCounterPlus"
mkdir -p "$PUB"
cp dist/ChestCounterPlus.exe "$PUB/"
crlf < packaging/LISEZMOI.txt > "$PUB/LISEZMOI.txt"
crlf < packaging/Desinstaller-ChestCounter-LED-N.bat > "$PUB/Desinstaller ChestCounter LED-N.bat"
printf '@echo off\nstart "" "%%~dp0ChestCounterPlus.exe"\n' | crlf > "$PUB/Lancer ChestCounter+.bat"
echo "Version publique à jour : Bureau\\ChestCounterPlus"
