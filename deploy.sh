#!/usr/bin/env bash
# Compile ChestCounter+ et l'installe dans Bureau\ChestCounterPlus (ferme l'app, puis relance l'attente de Fortnite).
set -euo pipefail
cd "$(dirname "$0")"
~/.dotnet/dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o dist
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "Get-Process ChestCounter,ChestCounterPlus -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3" || true
DEST='C:\Users\adm_loc\Desktop\ChestCounterPlus'
cp dist/ChestCounterPlus.exe "$(wslpath -u "$DEST")/"
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "Start-Process -FilePath '$DEST\ChestCounterPlus.exe' -ArgumentList '--standby','--data','\"C:\Users\adm_loc\AppData\Roaming\ChestCounterPlus\"'" || true
echo "Installé dans $DEST (attente de Fortnite relancée)"
