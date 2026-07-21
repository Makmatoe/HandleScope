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

$settingsDirectory = Get-HandleScopeLocalPath -RelativePath 'SessionDock'
$settingsPath = Assert-HandleScopeLocalPath -Path (
    Join-Path $settingsDirectory 'handlescope.json')
$settingsJson = "{`n  `"enabled`": true`n}`n"
$replaceExisting = $false
$migrateLegacyMinimalSetting = $false

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

    if (Test-HandleScopeMinimalSessionDockSetting -Setting $existing) {
        Write-Host "SessionDock integration is already enabled at $settingsPath"
        return
    }

    if (-not $Force) {
        throw 'SessionDock already has a handlescope.json file. Re-run with -Force only if replacing it with the minimal enabled setting is intended.'
    }

    $replaceExisting = $true
}
else {
    # Releases before the SessionDock rename wrote this minimal opt-in beneath
    # RobloxOne. It is only consulted when the canonical setting is absent, so
    # legacy state can never overwrite a current SessionDock configuration.
    $legacySettingsDirectory =
        Get-HandleScopeLocalPath -RelativePath 'RobloxOne'
    $legacySettingsPath = Assert-HandleScopeLocalPath -Path (
        Join-Path $legacySettingsDirectory 'handlescope.json')
    if (Test-Path -LiteralPath $legacySettingsPath) {
        $legacySettingsFile = Get-Item -LiteralPath $legacySettingsPath -Force
        Assert-HandleScopeFileSystemItemNotLink -Item $legacySettingsFile
        if ($legacySettingsFile.PSIsContainer -or
            $legacySettingsFile.Length -gt 4096) {
            throw 'The legacy SessionDock HandleScope setting is not a small regular file. The canonical setting was not changed.'
        }

        $legacySetting = $null
        try {
            $legacySetting =
                [IO.File]::ReadAllText($legacySettingsPath) |
                ConvertFrom-Json
        }
        catch {
            if (-not $Force) {
                throw 'The legacy SessionDock HandleScope setting is invalid JSON. The canonical setting was not changed. Re-run with -Force only if creating the minimal canonical setting is intended.'
            }
        }

        if (Test-HandleScopeMinimalSessionDockSetting `
                -Setting $legacySetting) {
            $migrateLegacyMinimalSetting = $true
        }
        elseif (-not $Force) {
            throw 'The legacy SessionDock handlescope.json is not the minimal opt-in. It was preserved and the canonical setting was not changed. Review it or re-run with -Force only if creating the minimal canonical setting is intended.'
        }
    }
}

$operation = if ($replaceExisting) {
    'Replace the existing SessionDock HandleScope setting with the minimal opt-in'
}
elseif ($migrateLegacyMinimalSetting) {
    'Copy the legacy minimal HandleScope opt-in to the canonical SessionDock path'
}
else {
    'Enable the optional HandleScope v1 integration for SessionDock'
}
if (-not $PSCmdlet.ShouldProcess(
        $settingsPath,
        $operation)) {
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
$backupPath = Assert-HandleScopeLocalPath -Path (
    Join-Path $settingsDirectory (
        'handlescope.' + [Guid]::NewGuid().ToString('N') + '.bak'))
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

    if ($replaceExisting) {
        if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
            throw 'The SessionDock HandleScope setting changed before it could be replaced.'
        }
        $settingsFile = Get-Item -LiteralPath $settingsPath -Force
        Assert-HandleScopeFileSystemItemNotLink -Item $settingsFile
        if ($settingsFile.PSIsContainer -or $settingsFile.Length -gt 4096) {
            throw 'The SessionDock HandleScope setting changed before it could be replaced.'
        }
        [IO.File]::Replace(
            $temporaryPath,
            $settingsPath,
            $backupPath,
            $true)
    }
    else {
        # File.Move has no overwrite mode on the supported Windows PowerShell
        # runtime. If another process creates the canonical file after our
        # checks, this fails closed instead of replacing that new file.
        [IO.File]::Move($temporaryPath, $settingsPath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-HandleScopeLocalItem -Path $temporaryPath
    }
    if (Test-Path -LiteralPath $backupPath) {
        Remove-HandleScopeLocalItem -Path $backupPath
    }
}

Write-Host "SessionDock integration enabled at $settingsPath"
if ($migrateLegacyMinimalSetting) {
    Write-Host 'The legacy minimal opt-in was copied without deleting or modifying legacy application data.'
}
Write-Host 'Start HandleScope.Api separately before launching through SessionDock.'
