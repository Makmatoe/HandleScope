[CmdletBinding()]
param(
    [string]$ProcessName,
    [int]$ProcessId,
    [string]$HandleName,
    [string]$HandleValue,
    [string]$Type,
    [string]$Access,
    [switch]$Exact,
    [switch]$DryRun,
    [switch]$CloseAll,
    [switch]$AllProcesses
)

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

$processIdSpecified = $PSBoundParameters.ContainsKey('ProcessId')
if ($processIdSpecified -and $ProcessId -le 0) {
    throw 'ProcessId must be a positive integer.'
}
if (-not $processIdSpecified -and
    $ProcessName -notin @('RobloxPlayerBeta', 'RobloxPlayerBeta.exe')) {
    throw "The release API permits only -ProcessName 'RobloxPlayerBeta'."
}
if ($processIdSpecified -and -not [string]::IsNullOrEmpty($ProcessName)) {
    throw 'Specify either -ProcessName or -ProcessId, not both.'
}
if ($AllProcesses -and $processIdSpecified) {
    throw '-AllProcesses cannot be combined with -ProcessId.'
}
if (-not $AllProcesses -and -not $processIdSpecified) {
    throw '-ProcessName requires -AllProcesses in the restricted release API.'
}
if (-not [string]::IsNullOrEmpty($HandleValue)) {
    throw 'Raw handle values are not accepted by the release API.'
}
if (-not $Exact) {
    throw '-Exact is required by the release API.'
}
if ($CloseAll) {
    throw '-CloseAll is not accepted by the release API.'
}
if ($HandleName -cnotmatch '^\\Sessions\\[1-9][0-9]*\\BaseNamedObjects\\ROBLOX_singletonEvent$') {
    throw 'HandleName must be the exact session-specific ROBLOX_singletonEvent path.'
}
if ($Type -cne 'Event') {
    throw "-Type must be 'Event'."
}
if ($Access -cnotmatch '^(?i:0x0*1f0003|2031619)$') {
    throw "-Access must be '0x001F0003'."
}

$connectionPath = Get-HandleScopeConnectionPath
$connection = Get-HandleScopeConnection -Path $connectionPath
if (-not (Test-HandleScopeApiConnection -Connection $connection)) {
    throw 'HandleScope API health or policy validation failed. No bearer token was sent.'
}
$processSelector = if ($processIdSpecified) {
    @{ pid = $ProcessId }
}
else {
    @{ name = 'RobloxPlayerBeta' }
}
$handleSelector = @{
    name = $HandleName
    match = 'exact'
    type = 'Event'
    access = '0x001F0003'
}

function New-HandleScopeBody {
    param([bool]$IsDryRun)

    return @{
        process = $processSelector
        handle = $handleSelector
        dryRun = $IsDryRun
        closeAll = $false
        allProcesses = [bool]$AllProcesses
    } | ConvertTo-Json -Depth 5 -Compress
}

$review = Invoke-HandleScopeApiRequest `
    -Connection $connection `
    -Path '/v1/handles/close' `
    -Method 'POST' `
    -Body (New-HandleScopeBody -IsDryRun $true) `
    -Authenticated
if ($review.StatusCode -ne 200 -or
    $null -eq $review.Json -or
    [int]$review.Json.matchCount -le 0 -or
    [int]$review.Json.failedCount -ne 0) {
    throw "HandleScope dry run was not approved (HTTP $($review.StatusCode)). No handle was closed."
}

if ($DryRun) {
    return $review.Json
}

$result = Invoke-HandleScopeApiRequest `
    -Connection $connection `
    -Path '/v1/handles/close' `
    -Method 'POST' `
    -Body (New-HandleScopeBody -IsDryRun $false) `
    -Authenticated
if ($result.StatusCode -ne 200 -or
    $null -eq $result.Json -or
    [int]$result.Json.closedCount -le 0 -or
    [int]$result.Json.failedCount -ne 0) {
    throw "HandleScope close did not complete cleanly (HTTP $($result.StatusCode))."
}

return $result.Json
