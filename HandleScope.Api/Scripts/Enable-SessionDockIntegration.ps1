[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$commonScript = Join-Path $PSScriptRoot 'HandleScope.ScriptCommon.ps1'
if (-not (Test-Path -LiteralPath $commonScript -PathType Leaf)) {
    throw 'HandleScope.ScriptCommon.ps1 was not found next to this script.'
}
. $commonScript

if (Test-HandleScopeAdministratorToken) {
    throw 'SessionDock integration is per user. Run this script from a normal PowerShell window.'
}

$settingsDirectory = Get-HandleScopeLocalPath -RelativePath 'RobloxOne'
$settingsPath = Assert-HandleScopeLocalPath -Path (
    Join-Path $settingsDirectory 'handlescope.json')
$settingsJson = "{`n  `"enabled`": true`n}`n"

if (Test-Path -LiteralPath $settingsPath) {
    $settingsFile = Get-Item -LiteralPath $settingsPath -Force
    Assert-HandleScopeFileSystemItemNotLink -Item $settingsFile
    if ($settingsFile.PSIsContainer -or $settingsFile.Length -gt 4096) {
        throw 'The existing SessionDock HandleScope setting is not a small regular file.'
    }

    $existing = $null
    try {
        $existing = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    }
    catch {
        if (-not $Force) {
            throw 'The existing SessionDock HandleScope setting is invalid JSON. Re-run with -Force only if replacing it with the minimal setting is intended.'
        }
    }

    $propertyNames = if ($null -eq $existing) {
        @()
    }
    else {
        @($existing.PSObject.Properties.Name)
    }
    if ($propertyNames.Count -eq 1 -and
        $propertyNames[0] -ceq 'enabled' -and
        $existing.enabled -is [bool] -and
        $existing.enabled -eq $true) {
        Write-Host "SessionDock integration is already enabled at $settingsPath"
        return
    }

    if (-not $Force) {
        throw 'SessionDock already has a handlescope.json file. Re-run with -Force only if replacing it with the minimal enabled setting is intended.'
    }
}

if (-not $PSCmdlet.ShouldProcess(
        $settingsPath,
        'Enable the optional HandleScope v1 integration for SessionDock')) {
    return
}

New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$settingsDirectory = Assert-HandleScopeLocalPath -Path $settingsDirectory
$directoryItem = Get-Item -LiteralPath $settingsDirectory -Force
Assert-HandleScopeFileSystemItemNotLink -Item $directoryItem
if (-not $directoryItem.PSIsContainer) {
    throw 'The SessionDock settings path is not a directory.'
}

$temporaryPath = Assert-HandleScopeLocalPath -Path (
    Join-Path $settingsDirectory (
        'handlescope.' + [Guid]::NewGuid().ToString('N') + '.tmp'))
try {
    $stream = [IO.FileStream]::new(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new(
            $stream,
            [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($settingsJson)
            $writer.Flush()
            $stream.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if (Test-Path -LiteralPath $settingsPath) {
        $settingsFile = Get-Item -LiteralPath $settingsPath -Force
        Assert-HandleScopeFileSystemItemNotLink -Item $settingsFile
    }
    Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-HandleScopeLocalItem -Path $temporaryPath
    }
}

Write-Host "SessionDock integration enabled at $settingsPath"
Write-Host 'Start HandleScope.Api separately before launching through SessionDock.'
