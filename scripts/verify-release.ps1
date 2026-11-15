# Local release-candidate verification (no GitHub Actions required)
# Run from repository root: pwsh -File scripts/verify-release.ps1
# Optional native Android workload: pwsh -File scripts/verify-release.ps1 -Android
param([switch]$Android, [switch]$WindowsNative)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repo

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host "`n=== $Label ==="
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (exit code $LASTEXITCODE). Release gate NOT PASSED."
    }
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is required.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to identify the tested commit.' }
$branchName = (& git branch --show-current).Trim()
$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Not a Git checkout.' }
Write-Host "Branch: $branchName"
Write-Host "Commit: $commit"
Write-Host 'This verifies offline builds and tests only. It does not authorize production.'

$tests = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Filter '*.test.cjs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($tests.Count -eq 0) { throw 'No Node regression tests found.' }
Invoke-Checked 'Node syntax (Stellar signer)' { & node --check 'src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js' }
Invoke-Checked 'Complete Node regression suite' { & node --test @tests }
Invoke-Checked 'Core Release build' { & dotnet build 'src/StellarVault.Core/StellarVault.Core.csproj' -c Release }
Invoke-Checked 'Shared UI Release build' { & dotnet build 'src/StellarVault.UI/StellarVault.UI.csproj' -c Release }
Invoke-Checked 'PWA Release build' { & dotnet build 'src/StellarVault.Pwa/StellarVault.Pwa.csproj' -c Release }
Invoke-Checked 'Core cryptography and linked-journal smoke' { & dotnet run --project 'tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj' -c Release }
if ($Android) {
    Invoke-Checked 'MAUI Android Release build' { & dotnet build 'src/StellarVault.Maui/StellarVault.Maui.csproj' -c Release -f net10.0-android }
}
if ($WindowsNative) {
    # Requires the MAUI Windows target and Windows SDK on this machine.
    Invoke-Checked 'MAUI Windows Release build' { & dotnet build 'src/StellarVault.Maui/StellarVault.Maui.csproj' -c Release -f net10.0-windows10.0.19041.0 }
}
Write-Host "`nOffline release-candidate checks PASSED for $commit."
Write-Host 'PRODUCTION GATE IS STILL CLOSED: real Testnet, device restart and independent audit remain mandatory.'
