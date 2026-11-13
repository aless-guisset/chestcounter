#!/usr/bin/env bash
# Prépare le dossier à partager (SwissTransfer…) : Bureau\ChestCounterPlus.zip.
set -euo pipefail
cd "$(dirname "$0")"
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
OUT=dist/ChestCounterPlus
rm -rf "$OUT" && mkdir -p "$OUT"
cp dist/ChestCounterPlus.exe "$OUT/"
crlf() { sed 's/\r*$/\r/' ; }
crlf < packaging/LISEZMOI.txt > "$OUT/LISEZMOI.txt"
crlf < packaging/Desinstaller-ChestCounter-LED-N.bat > "$OUT/Desinstaller ChestCounter LED-N.bat"
printf '@echo off\nstart "" "%%~dp0ChestCounterPlus.exe"\n' | crlf > "$OUT/Lancer ChestCounter+.bat"
crlf > "$OUT/Test lecture ecran (30 min).bat" <<'BAT'
@echo off
cd /d "%~dp0"
echo Lecture de l'ecran pendant 30 min : note le texte affiche quand tu ouvres un coffre.
echo PC : laisse vide (fenetre de Fortnite, en "Plein ecran fenetre").
echo Console : tape un mot du titre de la fenetre qui montre ton jeu (ex. OBS, Twitch, YouTube).
echo.
set "WIN="
set /p WIN=Fenetre (Entree = Fortnite) : 
if "%WIN%"=="" (start "" /wait /b ChestCounterPlus.exe --ocr-test 30) else (start "" /wait /b ChestCounterPlus.exe --ocr-test 30 "%WIN%")
start "" explorer "%APPDATA%\ChestCounterPlus\ocr"
pause
BAT
WIN_OUT=$(wslpath -w "$OUT")
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "\$z='C:\\Users\\adm_loc\\Desktop\\ChestCounterPlus.zip'; Remove-Item \$z -ErrorAction SilentlyContinue; Compress-Archive -Path '$WIN_OUT' -DestinationPath \$z; (Get-Item \$z).Length" | tr -d '\r'
echo "Prêt : Bureau\\ChestCounterPlus.zip"
