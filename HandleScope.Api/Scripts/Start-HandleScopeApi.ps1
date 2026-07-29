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
$executable = Join-Path $PSScriptRoot 'HandleScope.Api.exe'

function Test-HandleScopeApiReady {
    try {
        $script:activeConnection = Get-HandleScopeConnection -Path $connectionPath
        return Test-HandleScopeApiConnection -Connection $script:activeConnection
    }
    catch {
        return $false
    }
}

$connectionState = 'Missing'
$healthReady = $false
$referencedProcessState = 'None'
if (Test-Path -LiteralPath $connectionPath) {
    try {
        $script:activeConnection = Get-HandleScopeConnection `
            -Path $connectionPath
        $connectionState = 'Valid'
        $healthReady = Test-HandleScopeApiConnection `
            -Connection $script:activeConnection
        if (-not $healthReady) {
            $referencedProcessState =
                Get-HandleScopeReferencedApiProcessState -Path $connectionPath
        }
    }
    catch {
        $connectionState = 'Invalid'
        $referencedProcessState =
            Get-HandleScopeReferencedApiProcessState -Path $connectionPath
    }
}

$installedProcessState = if ($connectionState -ceq 'Valid' -and
    $healthReady) {
    'NotRunning'
}
else {
    Get-HandleScopeInstalledApiProcessState -ExecutablePath $executable
}
$startupDisposition = Resolve-HandleScopeApiStartupDisposition `
    -ConnectionState $connectionState `
    -HealthReady $healthReady `
    -ReferencedProcessState $referencedProcessState `
    -InstalledProcessState $installedProcessState

if ($startupDisposition -ceq 'Ready') {
    Write-Host 'HandleScope API is already running.'
    Write-HandleScopeConnectionSummary `
        -Connection $script:activeConnection `
        -Path $connectionPath
    return
}

if ($startupDisposition -ceq 'Blocked') {
    $blockReason = if ($connectionState -ceq 'Valid') {
        'the connection file identifies an API process whose health check failed'
    }
    elseif ($referencedProcessState -ceq 'Running') {
        'an API process referenced by the invalid connection file is still running'
    }
    elseif ($referencedProcessState -ceq 'Unknown') {
        'the process referenced by the invalid connection file could not be inspected safely'
    }
    elseif ($installedProcessState -ceq 'Running') {
        'the installed API executable is already running without a usable healthy connection'
    }
    else {
        'running API processes could not be inspected safely'
    }
    $recovery =
        "Wait a few seconds and retry. If the problem persists, run " +
        "Stop-HandleScopeApi.ps1. If authenticated shutdown is unavailable, " +
        "use Task Manager to stop only HandleScope.Api.exe running from " +
        "'$PSScriptRoot', then retry. No connection data was removed. " +
        "See $logPath"
    throw "HandleScope API startup was not attempted because $blockReason. $recovery"
}

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
    if (Test-HandleScopeApiReady) {
        Write-Host 'HandleScope API started in restricted standard-user mode.'
        Write-HandleScopeConnectionSummary `
            -Connection $script:activeConnection `
            -Path $connectionPath
        return
    }
}

throw "HandleScope API did not become ready. See $logPath"
