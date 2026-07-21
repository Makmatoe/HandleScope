[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$commonScript = Join-Path $PSScriptRoot 'HandleScope.ScriptCommon.ps1'
if (-not (Test-Path -LiteralPath $commonScript -PathType Leaf)) {
    throw 'HandleScope.ScriptCommon.ps1 was not found next to this script.'
}
. $commonScript

if (Test-HandleScopeAdministratorToken) {
    throw 'HandleScope client scripts refuse elevated execution. Open a normal PowerShell window and run this script again.'
}

$connectionPath = Get-HandleScopeConnectionPath

if (-not (Test-Path -LiteralPath $connectionPath -PathType Leaf)) {
    Write-Host 'HandleScope API is not running.'
    exit 0
}

try {
    $connection = Get-HandleScopeConnection -Path $connectionPath
}
catch {
    if (-not (Get-Process -Name 'HandleScope.Api' -ErrorAction SilentlyContinue)) {
        Remove-HandleScopeLocalItem -Path $connectionPath
        Write-Host 'Removed a stale HandleScope connection file; the API was not running.'
        exit 0
    }

    throw
}

$response = Invoke-HandleScopeApiRequest `
    -Connection $connection `
    -Path '/v1/shutdown' `
    -Method 'POST' `
    -Authenticated `
    -TimeoutSeconds 5 `
    -MaximumResponseBytes 65536
if ($response.StatusCode -ne 202) {
    throw "HandleScope API rejected the shutdown request (HTTP $($response.StatusCode))."
}

try {
    Wait-Process -Id $connection.processId -Timeout 10 -ErrorAction Stop
}
catch {
    if (Get-Process -Id $connection.processId -ErrorAction SilentlyContinue) {
        throw 'HandleScope API did not stop within 10 seconds.'
    }
}

Remove-HandleScopeLocalItem -Path $connectionPath -IgnoreMissing
Write-Host 'HandleScope API stopped.'
