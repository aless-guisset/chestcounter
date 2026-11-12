<# : batch
@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ([IO.File]::ReadAllText('%~f0', [Text.Encoding]::UTF8))"
pause
exit /b
#>
# Désinstalle le ChestCounter de LED-N (ChestCounter.exe). Ne touche jamais à ChestCounter+ (ChestCounterPlus.exe).

Write-Host "Désinstallation du ChestCounter de LED-N (ta version ChestCounter+ n'est pas touchée)`n"

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$found = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)

# 1. Où est l'exe : processus en cours, entrée « Ouvrir avec Fortnite », dossiers habituels.
Get-Process ChestCounter -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Path) { [void]$found.Add($_.Path) } }
$run = (Get-ItemProperty $runKey -Name ChestCounter -ErrorAction SilentlyContinue).ChestCounter
if ($run -match '^"([^"]+)"') { [void]$found.Add($Matches[1]) }
foreach ($dir in "$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", "$env:USERPROFILE\OneDrive\Desktop", "$env:USERPROFILE\OneDrive\Documents") {
    Get-ChildItem $dir -Filter ChestCounter.exe -Recurse -Depth 2 -ErrorAction SilentlyContinue | ForEach-Object { [void]$found.Add($_.FullName) }
}

# 2. Plus rien qui tourne ni qui redémarre avec Windows.
Get-Process ChestCounter -ErrorAction SilentlyContinue | Stop-Process -Force
if ($run) { Remove-ItemProperty $runKey -Name ChestCounter -ErrorAction SilentlyContinue; Write-Host "- Démarrage avec Windows retiré" }
Start-Sleep 1

# 3. Suppression de l'exe (et de l'ancien .old laissé par ses mises à jour).
$exes = @($found | Where-Object { Test-Path $_ })
if ($exes.Count -eq 0) {
    Write-Host "- Aucun ChestCounter.exe trouvé (déjà supprimé, ou rangé ailleurs : supprime-le à la main)."
} else {
    foreach ($exe in $exes) {
        Remove-Item $exe, "$exe.old" -Force -ErrorAction SilentlyContinue
        if (Test-Path $exe) { Write-Host "- IMPOSSIBLE de supprimer $exe (encore ouvert ?)" } else { Write-Host "- Supprimé : $exe" }
    }
}

# 4. Les données : gardées par défaut (ChestCounter+ les reprend à son premier lancement).
$data = Join-Path $env:APPDATA 'ChestCounter'
$plus = Join-Path $env:APPDATA 'ChestCounterPlus'
if (Test-Path $data) {
    Write-Host ""
    if (-not (Test-Path $plus)) {
        Write-Host "ATTENTION : ChestCounter+ n'a pas encore été lancé sur ce PC."
        Write-Host "Garde les données pour qu'il reprenne ton historique au premier lancement."
    }
    $r = Read-Host "Supprimer aussi l'historique et les réglages de LED-N ($data) ? (o/N)"
    if ($r -match '^[oOyY]') {
        Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $env:TEMP '.net\ChestCounter') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "- Données supprimées"
    } else {
        Write-Host "- Données gardées"
    }
}

Write-Host "`nTerminé."
