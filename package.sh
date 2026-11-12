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
crlf > "$OUT/Test XP (40 min).bat" <<'BAT'
@echo off
cd /d "%~dp0"
echo Releve XP et quetes du compte Epic toutes les 30 s pendant 40 min.
echo Il faut s'etre connecte a Epic dans ChestCounter+ (mode Console) avant.
echo Lance une partie, ouvre des coffres et note l'heure.
echo.
start "" /wait /b ChestCounterPlus.exe --ps5-xp 40
if not exist "Tests" mkdir "Tests"
copy /y "%APPDATA%\ChestCounterPlus\ps5\xp.txt" "Tests\xp.txt" >nul && start "" notepad "Tests\xp.txt"
if errorlevel 1 echo Pas de resultat : connecte-toi a Epic dans ChestCounter+ (mode Console) puis relance.
pause
BAT
WIN_OUT=$(wslpath -w "$OUT")
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "\$z='C:\\Users\\adm_loc\\Desktop\\ChestCounterPlus.zip'; Remove-Item \$z -ErrorAction SilentlyContinue; Compress-Archive -Path '$WIN_OUT' -DestinationPath \$z; (Get-Item \$z).Length" | tr -d '\r'
echo "Prêt : Bureau\\ChestCounterPlus.zip"
