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
$npmExecutable = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'npm.cmd' } else { 'npm' }
if (-not (Get-Command $npmExecutable -ErrorAction SilentlyContinue)) { throw 'npm is required for dependency advisory checks.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to identify the tested commit.' }
$branchName = (@(& git branch --show-current) -join '').Trim()
if (-not $branchName) { $branchName = '(detached HEAD)' }
$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Not a Git checkout.' }
$dirty = @(& git status --porcelain).Count -gt 0
Write-Host "Branch: $branchName"
Write-Host "Commit: $commit"
if ($dirty) { Write-Host 'Worktree contains uncommitted files: results apply to this worktree, not the commit alone.' }
Write-Host 'This verifies offline builds and tests only. It does not authorize production.'

$tests = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Filter '*.test.cjs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($tests.Count -eq 0) { throw 'No Node regression tests found.' }
Invoke-Checked 'Node syntax (Stellar signer)' { & node --check 'src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js' }
Invoke-Checked 'Vendored signer provenance' { & node 'scripts/verify-vendor.cjs' }
Invoke-Checked 'Landing assets and bundled font integrity' { & node 'scripts/verify-landing.cjs' }
Invoke-Checked 'JavaScript dependency advisories' { & $npmExecutable audit --registry=https://registry.npmjs.org --audit-level=low }
Invoke-Checked 'Complete Node regression suite' { & node --test @tests }
Invoke-Checked 'PWA dependency security audit' { & dotnet restore 'src/StellarVault.Pwa/StellarVault.Pwa.csproj' -p:NuGetAudit=true -p:NuGetAuditMode=all '-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905' }
Invoke-Checked 'API dependency security audit' { & dotnet restore 'src/StellarVault.Api/StellarVault.Api.csproj' -p:NuGetAudit=true -p:NuGetAuditMode=all '-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905' }
Invoke-Checked 'Standalone engine tests' { & dotnet run --project 'tests/VaultEngine.Core.Tests/VaultEngine.Core.Tests.csproj' -c Release }
Invoke-Checked 'Stellar provider Release build' { & dotnet build 'src/VaultEngine.Stellar/VaultEngine.Stellar.csproj' -c Release }
Invoke-Checked 'Stellar Horizon integration Release build' { & dotnet build 'src/VaultEngine.Stellar.Horizon/VaultEngine.Stellar.Horizon.csproj' -c Release }
Invoke-Checked 'API Release build' { & dotnet build 'src/StellarVault.Api/StellarVault.Api.csproj' -c Release }
Invoke-Checked 'API network boundary tests' { & dotnet run --project 'tests/StellarVault.Api.Tests/StellarVault.Api.Tests.csproj' -c Release }
Invoke-Checked 'PWA S-key JS interop contract' { & dotnet run --project 'tests/StellarVault.Pwa.InteropTests/StellarVault.Pwa.InteropTests.csproj' -c Release }
if ([Environment]::OSVersion.Platform -eq 'Win32NT') {
    Invoke-Checked 'Authenticated API HTTP smoke' { & powershell -NoProfile -File 'scripts/verify-api-http.ps1' }
}
Invoke-Checked 'Shared UI Release build' { & dotnet build 'src/StellarVault.UI/StellarVault.UI.csproj' -c Release }
Invoke-Checked 'PWA Release build' { & dotnet build 'src/StellarVault.Pwa/StellarVault.Pwa.csproj' -c Release }
Invoke-Checked 'PWA Release publish' { & dotnet publish 'src/StellarVault.Pwa/StellarVault.Pwa.csproj' -c Release --no-restore -o 'artifacts/release/pwa' }
Invoke-Checked 'Published PWA integrity' { & node 'scripts/verify-published-pwa.cjs' 'artifacts/release/pwa/wwwroot' }
Invoke-Checked 'Core cryptography and linked-journal smoke' { & dotnet run --project 'tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj' -c Release }
if ($Android) {
    Invoke-Checked 'MAUI Android Release build' { & dotnet build 'src/StellarVault.Maui/StellarVault.Maui.csproj' -c Release -f net10.0-android -p:VaultBuildTargetFramework=net10.0-android }
}
if ($WindowsNative) {
    # Requires the MAUI Windows target and Windows SDK on this machine.
    Invoke-Checked 'MAUI Windows Release build' { & dotnet build 'src/StellarVault.Maui/StellarVault.Maui.csproj' -c Release -f net10.0-windows10.0.19041.0 -p:VaultBuildTargetFramework=net10.0-windows10.0.19041.0 }
}
Write-Host "`nOffline release-candidate checks PASSED for worktree based on $commit (dirty: $dirty)."
Write-Host 'PRODUCTION GATE IS STILL CLOSED: real Testnet, device restart and independent audit remain mandatory.'
