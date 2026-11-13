#!/usr/bin/env bash
# Prépare la version publique à partager : le dossier Bureau\ChestCounterPlus (exe, lancement, désinstalleur LED-N, LISEZMOI).
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
# Version publique : un dossier (pas de zip, le partage ne les accepte pas), même nom que l'appli.
PUB=/mnt/c/Users/adm_loc/Desktop/ChestCounterPlus
mkdir -p "$PUB"
cp -r "$OUT"/. "$PUB"/
echo "Prêt : Bureau\\ChestCounterPlus (version publique, à partager tel quel)"
