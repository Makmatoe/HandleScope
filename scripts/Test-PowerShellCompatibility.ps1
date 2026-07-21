[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$commonScript = Join-Path `
    $repositoryRoot `
    'HandleScope.Api\Scripts\HandleScope.ScriptCommon.ps1'
. $commonScript

$minimal = '{"enabled":true}' | ConvertFrom-Json
if (-not (Test-HandleScopeMinimalSessionDockSetting -Setting $minimal)) {
    throw 'The minimal enabled SessionDock setting was not recognized.'
}

$disabled = '{"enabled":false}' | ConvertFrom-Json
if (Test-HandleScopeMinimalSessionDockSetting -Setting $disabled) {
    throw 'A disabled SessionDock setting was incorrectly accepted.'
}

$extended = '{"enabled":true,"unexpected":true}' | ConvertFrom-Json
if (Test-HandleScopeMinimalSessionDockSetting -Setting $extended) {
    throw 'An extended SessionDock setting was incorrectly accepted.'
}

if (Test-HandleScopeMinimalSessionDockSetting -Setting $null) {
    throw 'A missing SessionDock setting was incorrectly accepted.'
}

Write-Host 'Windows PowerShell SessionDock setting compatibility validation passed.'
