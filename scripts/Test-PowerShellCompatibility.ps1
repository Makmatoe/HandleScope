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

function Assert-MinimalSessionDockSettingFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected SessionDock setting was not created: $Path"
    }
    $setting = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    if (-not (Test-HandleScopeMinimalSessionDockSetting -Setting $setting)) {
        throw "SessionDock setting was not the minimal enabled opt-in: $Path"
    }
}

$integrationTestRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ('HandleScope-SessionDock-' + [Guid]::NewGuid().ToString('N'))
$isolatedScriptRoot = Join-Path $integrationTestRoot 'scripts'
$helperSource = Join-Path `
    $repositoryRoot `
    'HandleScope.Api\Scripts\Enable-SessionDockIntegration.ps1'
$helperUnderTest = Join-Path `
    $isolatedScriptRoot `
    'Enable-SessionDockIntegration.ps1'
$mockCommonPath = Join-Path `
    $isolatedScriptRoot `
    'HandleScope.ScriptCommon.ps1'
$previousTestRoot = $env:HANDLESCOPE_INTEGRATION_TEST_ROOT

try {
    New-Item -ItemType Directory -Path $isolatedScriptRoot -Force | Out-Null
    Copy-Item -LiteralPath $helperSource -Destination $helperUnderTest
    $mockCommon = @'
Set-StrictMode -Version Latest

function Test-HandleScopeAdministratorToken { return $false }

function Test-HandleScopeMinimalSessionDockSetting {
    param([AllowNull()][object]$Setting)
    if ($null -eq $Setting) { return $false }
    $propertyNames = @($Setting.PSObject.Properties.Name)
    return $propertyNames.Length -eq 1 -and
        $propertyNames[0] -ceq 'enabled' -and
        $Setting.enabled -is [bool] -and
        $Setting.enabled -eq $true
}

function Assert-HandleScopeFileSystemItemNotLink {
    param([Parameter(Mandatory)][IO.FileSystemInfo]$Item)
    $linkType = $Item.PSObject.Properties['LinkType']
    if (($null -ne $linkType -and
         -not [string]::IsNullOrWhiteSpace([string]$linkType.Value)) -or
        (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'Test path contains a file-system link or reparse point.'
    }
}

function Assert-HandleScopeLocalPath {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath(
        $env:HANDLESCOPE_INTEGRATION_TEST_ROOT).TrimEnd('\', '/')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test helper path escaped its isolated root.'
    }
    return $fullPath
}

function Get-HandleScopeLocalPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    return Assert-HandleScopeLocalPath -Path (
        Join-Path $env:HANDLESCOPE_INTEGRATION_TEST_ROOT $RelativePath)
}

function Remove-HandleScopeLocalItem {
    param([Parameter(Mandatory)][string]$Path)
    $safePath = Assert-HandleScopeLocalPath -Path $Path
    Remove-Item -LiteralPath $safePath -Force
}
'@
    [IO.File]::WriteAllText(
        $mockCommonPath,
        $mockCommon,
        [Text.UTF8Encoding]::new($false))

    $emptyScenario = Join-Path $integrationTestRoot 'empty'
    New-Item -ItemType Directory -Path $emptyScenario | Out-Null
    $env:HANDLESCOPE_INTEGRATION_TEST_ROOT = $emptyScenario
    & $helperUnderTest -Confirm:$false
    Assert-MinimalSessionDockSettingFile -Path (
        Join-Path $emptyScenario 'SessionDock\handlescope.json')

    $legacyScenario = Join-Path $integrationTestRoot 'legacy-minimal'
    $legacyDirectory = Join-Path $legacyScenario 'RobloxOne'
    New-Item -ItemType Directory -Path $legacyDirectory -Force | Out-Null
    $legacyPath = Join-Path $legacyDirectory 'handlescope.json'
    $legacyContent = "{`n  `"enabled`": true`n}`n"
    [IO.File]::WriteAllText(
        $legacyPath,
        $legacyContent,
        [Text.UTF8Encoding]::new($false))
    $env:HANDLESCOPE_INTEGRATION_TEST_ROOT = $legacyScenario
    & $helperUnderTest -Confirm:$false
    Assert-MinimalSessionDockSettingFile -Path (
        Join-Path $legacyScenario 'SessionDock\handlescope.json')
    if ([IO.File]::ReadAllText($legacyPath) -cne $legacyContent) {
        throw 'Migrating a legacy minimal opt-in changed the legacy file.'
    }

    $canonicalScenario = Join-Path $integrationTestRoot 'canonical-wins'
    $canonicalDirectory = Join-Path $canonicalScenario 'SessionDock'
    $oldDirectory = Join-Path $canonicalScenario 'RobloxOne'
    New-Item -ItemType Directory -Path $canonicalDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $oldDirectory -Force | Out-Null
    $canonicalPath = Join-Path $canonicalDirectory 'handlescope.json'
    $canonicalContent = '{"enabled":false,"retryTimeoutSeconds":17}'
    $oldPath = Join-Path $oldDirectory 'handlescope.json'
    [IO.File]::WriteAllText(
        $canonicalPath,
        $canonicalContent,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $oldPath,
        $legacyContent,
        [Text.UTF8Encoding]::new($false))
    $env:HANDLESCOPE_INTEGRATION_TEST_ROOT = $canonicalScenario
    $canonicalConflictRejected = $false
    try {
        & $helperUnderTest -Confirm:$false
    }
    catch {
        $canonicalConflictRejected = $true
    }
    if (-not $canonicalConflictRejected) {
        throw 'A non-minimal canonical SessionDock setting was overwritten without -Force.'
    }
    if ([IO.File]::ReadAllText($canonicalPath) -cne $canonicalContent -or
        [IO.File]::ReadAllText($oldPath) -cne $legacyContent) {
        throw 'A rejected legacy migration changed canonical or legacy data.'
    }
    & $helperUnderTest -Force -Confirm:$false
    Assert-MinimalSessionDockSettingFile -Path $canonicalPath
    $forcedCanonicalContent = [IO.File]::ReadAllText($canonicalPath)
    $replacementArtifacts = @(Get-ChildItem `
        -LiteralPath $canonicalDirectory `
        -File `
        -Force |
        Where-Object { $_.Name -cne 'handlescope.json' })
    if ($replacementArtifacts.Count -ne 0) {
        throw 'Atomic canonical replacement left a temporary or backup file.'
    }
    if ([IO.File]::ReadAllText($oldPath) -cne $legacyContent) {
        throw 'An explicit canonical replacement changed the legacy file.'
    }
    & $helperUnderTest -Confirm:$false
    if ([IO.File]::ReadAllText($canonicalPath) -cne
        $forcedCanonicalContent) {
        throw 'Re-running the helper changed an enabled minimal setting.'
    }

    $legacyConflictScenario = Join-Path `
        $integrationTestRoot `
        'legacy-nonminimal'
    $legacyConflictDirectory = Join-Path `
        $legacyConflictScenario `
        'RobloxOne'
    New-Item `
        -ItemType Directory `
        -Path $legacyConflictDirectory `
        -Force |
        Out-Null
    $legacyConflictPath = Join-Path `
        $legacyConflictDirectory `
        'handlescope.json'
    $legacyConflictContent = '{"enabled":true,"retryTimeoutSeconds":17}'
    [IO.File]::WriteAllText(
        $legacyConflictPath,
        $legacyConflictContent,
        [Text.UTF8Encoding]::new($false))
    $env:HANDLESCOPE_INTEGRATION_TEST_ROOT = $legacyConflictScenario
    $legacyConflictRejected = $false
    try {
        & $helperUnderTest -Confirm:$false
    }
    catch {
        $legacyConflictRejected = $true
    }
    if (-not $legacyConflictRejected -or
        (Test-Path -LiteralPath (
            Join-Path $legacyConflictScenario 'SessionDock\handlescope.json')) -or
        [IO.File]::ReadAllText($legacyConflictPath) -cne
            $legacyConflictContent) {
        throw 'A non-minimal legacy setting was not preserved and rejected.'
    }
    & $helperUnderTest -Force -Confirm:$false
    Assert-MinimalSessionDockSettingFile -Path (
        Join-Path $legacyConflictScenario 'SessionDock\handlescope.json')
    if ([IO.File]::ReadAllText($legacyConflictPath) -cne
        $legacyConflictContent) {
        throw 'Explicit canonical creation changed a non-minimal legacy setting.'
    }
}
finally {
    if ($null -eq $previousTestRoot) {
        Remove-Item Env:HANDLESCOPE_INTEGRATION_TEST_ROOT `
            -ErrorAction SilentlyContinue
    }
    else {
        $env:HANDLESCOPE_INTEGRATION_TEST_ROOT = $previousTestRoot
    }
    if (Test-Path -LiteralPath $integrationTestRoot) {
        Remove-Item `
            -LiteralPath $integrationTestRoot `
            -Recurse `
            -Force
    }
}

Write-Host 'Windows PowerShell SessionDock setting compatibility validation passed.'
