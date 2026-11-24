$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$apiRoot = Join-Path $repo 'src/StellarVault.Api'
$dll = Join-Path $apiRoot 'bin/Release/net10.0/StellarVault.Api.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build the API Release configuration first.' }
function Assert-Status([string]$Url, [int]$Expected, [hashtable]$Headers = @{}, [switch]$Plain, [string]$Method = 'GET') {
    if (-not $Plain -and -not $Headers.ContainsKey('X-Forwarded-Proto')) { $Headers['X-Forwarded-Proto'] = 'https' }
    try {
        $response = Invoke-WebRequest $Url -Headers $Headers -Method $Method -UseBasicParsing -TimeoutSec 10
        if ($response.StatusCode -ne $Expected) { throw "Expected HTTP $Expected." }
    } catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne $Expected) { throw }
    }
}
# An ephemeral high-entropy test key; only its digest is given to the child host.
$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$testKey = ([BitConverter]::ToString($keyBytes)).Replace('-', '').ToLowerInvariant()
$sha = [Security.Cryptography.SHA256]::Create()
$keyDigest = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($testKey)))).Replace('-', '')
$variables = @{
    'ASPNETCORE_ENVIRONMENT' = 'Production'
    'ASPNETCORE_FORWARDEDHEADERS_ENABLED' = 'false'
    'DOTNET_FORWARDEDHEADERS_ENABLED' = 'false'
    'ApiHosting__TrustedProxies__0' = '127.0.0.1'
    'ApiAccess__Tenants__0__Id' = 'http-smoke'
    'ApiAccess__Tenants__0__KeySha256__0' = $keyDigest
    'ApiAccess__Tenants__0__Networks__0' = 'testnet'
    'ApiLimits__GlobalRequestsPerMinute' = '120'
    'ApiLimits__TenantRequestsPerMinute' = '20'
    'ApiLimits__GlobalConcurrency' = '8'
    'ApiLimits__TenantConcurrency' = '2'
}
$previous = @{}
$server = $null
try {
    foreach ($name in $variables.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    $server = Start-Process -FilePath 'dotnet' -ArgumentList @('"' + $dll + '"', '--urls', 'http://127.0.0.1:15080', '--contentRoot', '"' + $apiRoot + '"') -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($server.HasExited) { throw 'API process exited before becoming ready.' }
        try {
            $health = Invoke-WebRequest 'http://127.0.0.1:15080/health/live' -UseBasicParsing -TimeoutSec 1
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'API did not start.' }
    Assert-Status 'http://127.0.0.1:15080/health/ready' 200
    Assert-Status 'http://127.0.0.1:15080/v1/capabilities' 401
    Assert-Status 'http://127.0.0.1:15080/v1/networks' 401 @{ Authorization = ('Bearer ' + ('0' * 64)) }
    Assert-Status 'http://127.0.0.1:15080/v1/capabilities' 400 @{} -Plain
    Assert-Status 'http://127.0.0.1:15080/v1/capabilities' 400 @{ 'X-Forwarded-Proto' = 'https, http' }
    $headers = @{ Authorization = ('Bearer ' + $testKey); 'X-Forwarded-Proto' = 'https' }
    $secureResponse = Invoke-WebRequest 'http://127.0.0.1:15080/v1/capabilities' -Headers $headers -UseBasicParsing
    if ($secureResponse.Headers['Cache-Control'] -notmatch 'no-store' -or $secureResponse.Headers['X-Frame-Options'] -ne 'DENY') { throw 'API security headers missing.' }
    $capabilities = Invoke-RestMethod 'http://127.0.0.1:15080/v1/capabilities' -Headers $headers
    if ($capabilities.engine -ne 'VaultEngine.Core') { throw 'API is not using the shared engine assembly.' }
    if ($capabilities.storageWrites -or $capabilities.mutationConfirmation) { throw 'Unsafe capabilities exposed.' }
    $networks = @(Invoke-RestMethod 'http://127.0.0.1:15080/v1/networks' -Headers $headers)
    if ('testnet' -notin $networks.Id -or 'public' -in $networks.Id) { throw 'Tenant discovery scope violated.' }
    $hash = 'a' * 64
    Assert-Status "http://127.0.0.1:15080/v1/networks/public/transactions/$hash" 404 $headers
    Assert-Status "http://127.0.0.1:15080/v1/networks/unknown/transactions/$hash" 404 $headers
    Assert-Status 'http://127.0.0.1:15080/v1/networks/testnet/transactions/invalid' 400 $headers
    Assert-Status 'http://127.0.0.1:15080/v1/networks/testnet/transactions/invalid' 401
    Assert-Status 'http://127.0.0.1:15080/v1/capabilities' 405 $headers -Method POST
    $limited = $false
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        try {
            Invoke-WebRequest 'http://127.0.0.1:15080/v1/capabilities' -Headers $headers -UseBasicParsing -TimeoutSec 10 | Out-Null
        } catch {
            if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 429) {
                if ([int]$_.Exception.Response.Headers['Retry-After'] -lt 1) { throw 'Rate rejection has no usable Retry-After.' }
                $limited = $true
                break
            }
            throw
        }
    }
    if (-not $limited) { throw 'Tenant HTTP request budget was not enforced.' }
    Assert-Status 'http://127.0.0.1:15080/health/ready' 200
    Write-Host 'PASS: HTTP auth, tenant isolation, HTTPS/security headers, read-only methods, request budgets and readiness under throttling.'
} finally {
    if ($null -ne $server) {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id }
        $server.Dispose()
    }
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    $testKey = $null
    $rng.Dispose()
    $sha.Dispose()
}
