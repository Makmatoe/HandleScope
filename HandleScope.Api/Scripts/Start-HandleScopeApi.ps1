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
    throw 'HandleScope.Api refuses elevated execution. Open a normal PowerShell window and run this script again.'
}

$connectionPath = Get-HandleScopeConnectionPath
$logPath = Get-HandleScopeLogPath

function Test-HandleScopeApi {
    try {
        $script:activeConnection = Get-HandleScopeConnection -Path $connectionPath
        return Test-HandleScopeApiConnection -Connection $script:activeConnection
    }
    catch {
        return $false
    }
}

if (Test-HandleScopeApi) {
    Write-Host 'HandleScope API is already running.'
    Write-HandleScopeConnectionSummary `
        -Connection $script:activeConnection `
        -Path $connectionPath
    exit 0
}

if (Test-Path -LiteralPath $connectionPath) {
    Remove-HandleScopeLocalItem -Path $connectionPath
}

$executable = Join-Path $PSScriptRoot 'HandleScope.Api.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'HandleScope.Api.exe was not found next to this script. Run the installer from the complete verified release bundle first.'
}

Start-Process `
    -FilePath $executable `
    -WorkingDirectory $PSScriptRoot `
    -WindowStyle Hidden

$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if (Test-HandleScopeApi) {
        Write-Host 'HandleScope API started in restricted standard-user mode.'
        Write-HandleScopeConnectionSummary `
            -Connection $script:activeConnection `
            -Path $connectionPath
        exit 0
    }
}

throw "HandleScope API did not become ready. See $logPath"
