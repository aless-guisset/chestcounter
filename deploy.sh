#!/usr/bin/env bash
# Compile ChestCounter et l'installe dans C:\Users\adm_loc\ChestCounterPC (ferme l'app si elle tourne).
set -euo pipefail
cd "$(dirname "$0")"
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "Get-Process ChestCounter,ChestCounterPlus -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1" || true
cp dist/ChestCounterPlus.exe /mnt/c/Users/adm_loc/ChestCounterPC/
echo "Installé dans C:\\Users\\adm_loc\\ChestCounterPC"
