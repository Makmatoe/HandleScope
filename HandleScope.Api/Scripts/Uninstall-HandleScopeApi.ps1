[CmdletBinding()]
param(
    [switch]$KeepDiagnostics
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$commonScript = Join-Path $PSScriptRoot 'HandleScope.ScriptCommon.ps1'
if (-not (Test-Path -LiteralPath $commonScript -PathType Leaf)) {
    throw 'HandleScope.ScriptCommon.ps1 was not found next to this script.'
}
. $commonScript

if (Test-HandleScopeAdministratorToken) {
    throw 'HandleScope is installed per user. Run this script from a normal PowerShell window.'
}

$installRoot = Get-HandleScopeInstallRoot
$stopScript = Join-Path $installRoot 'Stop-HandleScopeApi.ps1'
$stopScript = Assert-HandleScopeLocalPath -Path $stopScript
if (-not (Test-Path -LiteralPath $stopScript -PathType Leaf)) {
    $stopScript = Join-Path $PSScriptRoot 'Stop-HandleScopeApi.ps1'
}
if (Test-Path -LiteralPath $stopScript -PathType Leaf) {
    & $stopScript
}

$taskIdentity = Get-HandleScopeTaskIdentity
$task = Get-ScheduledTask `
    -TaskName $taskIdentity.TaskName `
    -TaskPath $taskIdentity.TaskPath `
    -ErrorAction SilentlyContinue
if ($null -ne $task) {
    if ($task.Actions.Count -ne 1) {
        throw 'Refusing to remove an unexpected HandleScope scheduled task.'
    }
    $taskExecutable = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables(
            [string]$task.Actions[0].Execute))
    $expectedExecutable = [IO.Path]::GetFullPath(
        (Join-Path $installRoot 'HandleScope.Api.exe'))
    if ($taskExecutable -cne $expectedExecutable -or
        $task.Principal.RunLevel -ne 'Limited') {
        throw 'Refusing to remove a task whose action or privilege level is unexpected.'
    }

    Unregister-ScheduledTask `
        -TaskName $taskIdentity.TaskName `
        -TaskPath $taskIdentity.TaskPath `
        -Confirm:$false
}

$localPrograms = Get-HandleScopeLocalProgramsRoot
if (-not $installRoot.StartsWith(
        $localPrograms + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a path outside the current user's Programs directory: $installRoot"
}

Remove-HandleScopeLocalItem `
    -Path $installRoot `
    -Recurse `
    -IgnoreMissing

if (-not $KeepDiagnostics) {
    $runtimeRoot = Get-HandleScopeRuntimeRoot
    Remove-HandleScopeLocalItem `
        -Path $runtimeRoot `
        -Recurse `
        -IgnoreMissing
}

Write-Host 'HandleScope API autostart, files, and local runtime data were removed.'
