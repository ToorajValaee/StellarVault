param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]{1,64}$')][string]$TenantId,
    [Parameter(Mandatory = $true)][string[]]$Networks
)
$ErrorActionPreference = 'Stop'
if ($TenantId -cnotmatch '^[a-z0-9-]{1,64}$') { throw 'Tenant ID must be lowercase.' }
if ($Networks.Count -lt 1 -or $Networks.Count -gt 128 -or
    @($Networks | Where-Object { $_ -cnotmatch '^[a-z0-9-]{1,64}$' }).Count -ne 0 -or
    @($Networks | Select-Object -Unique).Count -ne $Networks.Count) { throw 'Provide unique configured network IDs.' }
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $rng.GetBytes($bytes)
    $key = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key)))).Replace('-', '').ToLowerInvariant()
    # Intentionally return the one-time secret to the operator. Never persist it in appsettings.
    [pscustomobject]@{
        ApiKey = $key
        TenantConfiguration = [pscustomobject]@{ Id = $TenantId; KeySha256 = @($hash); Networks = $Networks }
    } | ConvertTo-Json -Depth 4
} finally {
    [Array]::Clear($bytes, 0, $bytes.Length)
    $key = $null
    $rng.Dispose()
    $sha.Dispose()
}
