[CmdletBinding()]
param(
    [switch]$EnableAutostart,
    [switch]$StartNow,
    [switch]$AllowDowngrade,
    [switch]$VerifyOnly
)

Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$firstPartyFiles = @(
    'HandleScope.Api.exe',
    'Enable-SessionDockIntegration.ps1',
    'HandleScope.ScriptCommon.ps1',
    'Install-HandleScopeApi.ps1',
    'Invoke-HandleScopeClose.ps1',
    'Start-HandleScopeApi.ps1',
    'Stop-HandleScopeApi.ps1',
    'Uninstall-HandleScopeApi.ps1'
)

$sourceDirectory = [IO.DirectoryInfo]::new(
    [IO.Path]::GetFullPath($PSScriptRoot))
if (-not $sourceDirectory.Exists) {
    throw 'The API release directory does not exist.'
}
$sourceAncestor = $sourceDirectory
while ($null -ne $sourceAncestor) {
    if (($sourceAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The API release directory cannot be inside a reparse-point path.'
    }
    $sourceAncestor = $sourceAncestor.Parent
}

$bundleDirectory = $sourceDirectory.Parent
if ($null -eq $bundleDirectory) {
    throw 'The API release directory has no bundle parent.'
}
$bundleRoot = $bundleDirectory.FullName
$contentsManifest = [IO.Path]::Combine($bundleRoot, 'CONTENTS.sha256')
$manifestFile = [IO.FileInfo]::new($contentsManifest)
if (-not $manifestFile.Exists) {
    throw 'The release contents manifest is missing. Install only from the complete verified release ZIP.'
}
if (($manifestFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The release contents manifest cannot be a reparse point.'
}

$expectedSourceFileNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($fileName in $firstPartyFiles) {
    [void]$expectedSourceFileNames.Add($fileName)
}
[void]$expectedSourceFileNames.Add('API.md')

$sourceItems = @($sourceDirectory.GetFileSystemInfos())
if ($sourceItems.Count -ne $expectedSourceFileNames.Count) {
    throw 'The API release directory does not match the fixed nine-file allowlist.'
}
$sourceFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($sourceItem in $sourceItems) {
    if ($sourceItem -isnot [IO.FileInfo] -or
        ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not $expectedSourceFileNames.Contains($sourceItem.Name)) {
        throw "Unexpected or linked item in the API release directory: $($sourceItem.Name)"
    }
    $sourceFiles.Add($sourceItem)
}

$manifestEntries = [Collections.Generic.SortedDictionary[string, string]]::new(
    [StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadAllLines($contentsManifest)) {
    if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<path>[^\\\r\n]+)$') {
        throw 'The release contents manifest is malformed.'
    }
    if ($Matches.path.StartsWith('api/', [StringComparison]::Ordinal)) {
        if ($manifestEntries.ContainsKey($Matches.path)) {
            throw 'The release contents manifest contains a duplicate API path.'
        }
        $manifestEntries.Add($Matches.path, $Matches.hash)
    }
}

if ($manifestEntries.Count -ne $sourceFiles.Count) {
    throw 'The API directory does not match the reviewed release manifest.'
}
foreach ($sourceFile in $sourceFiles) {
    $relativePath = "api/$($sourceFile.Name)"
    if (-not $manifestEntries.ContainsKey($relativePath)) {
        throw "Unexpected file in the API release directory: $($sourceFile.Name)"
    }
    $actualHash = (Microsoft.PowerShell.Utility\Get-FileHash `
        -LiteralPath $sourceFile.FullName `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $manifestEntries[$relativePath]) {
        throw "Release integrity check failed for $($sourceFile.Name)."
    }
}

if ($VerifyOnly) {
    Microsoft.PowerShell.Utility\Write-Host `
        'HandleScope API release inventory and manifest hashes are valid.'
    return
}

$commonScript = [IO.Path]::Combine(
    $PSScriptRoot,
    'HandleScope.ScriptCommon.ps1')
. $commonScript

if (Test-HandleScopeAdministratorToken) {
    throw 'HandleScope uses a per-user, standard-privilege installation. Run this script from a normal PowerShell window.'
}

$legacyTask = Get-ScheduledTask `
    -TaskName 'HandleScope Local API' `
    -ErrorAction SilentlyContinue
if ($null -ne $legacyTask) {
    throw 'A legacy development-only elevated HandleScope task is still installed. Remove it with the matching legacy uninstall script before installing this release.'
}

$sourceExecutable = Join-Path $PSScriptRoot 'HandleScope.Api.exe'
$installRoot = Get-HandleScopeInstallRoot
$installParent = Assert-HandleScopeLocalPath -Path (Split-Path -Parent $installRoot)
$localPrograms = Get-HandleScopeLocalProgramsRoot
if (-not $installRoot.StartsWith(
        $localPrograms + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install outside the current user's Programs directory: $installRoot"
}

if (Test-Path -LiteralPath $installRoot) {
    $installRoot = Assert-HandleScopeLocalTreeSafe -Path $installRoot
}

$installedExecutable = Join-Path $installRoot 'HandleScope.Api.exe'
if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
    $sourceVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo(
        $sourceExecutable).FileVersion)
    $installedVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo(
        $installedExecutable).FileVersion)
    if (-not $AllowDowngrade -and $sourceVersion -lt $installedVersion) {
        throw "Refusing to downgrade HandleScope.Api from $installedVersion to $sourceVersion."
    }

    & (Join-Path $PSScriptRoot 'Stop-HandleScopeApi.ps1')
}

New-Item -ItemType Directory -Path $installParent -Force | Out-Null
$installParent = Assert-HandleScopeLocalPath -Path $installParent
$stagingRoot = Assert-HandleScopeLocalPath -Path (Join-Path `
    $installParent `
    ("Api.staging." + [Guid]::NewGuid().ToString('N')))
$backupRoot = Assert-HandleScopeLocalPath -Path (Join-Path `
    $installParent `
    ("Api.backup." + [Guid]::NewGuid().ToString('N')))

try {
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    $stagingRoot = Assert-HandleScopeLocalTreeSafe -Path $stagingRoot
    foreach ($sourceFile in $sourceFiles) {
        Copy-Item `
            -LiteralPath $sourceFile.FullName `
            -Destination (Join-Path $stagingRoot $sourceFile.Name)
    }

    foreach ($sourceFile in $sourceFiles) {
        $copiedPath = Join-Path $stagingRoot $sourceFile.Name
        $relativePath = "api/$($sourceFile.Name)"
        $copiedHash = (Get-FileHash `
            -LiteralPath $copiedPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $manifestEntries.ContainsKey($relativePath) -or
            $copiedHash -cne $manifestEntries[$relativePath]) {
            throw "Installed-file verification failed for $($sourceFile.Name)."
        }
    }
    if (Test-Path -LiteralPath $installRoot) {
        $installRoot = Assert-HandleScopeLocalTreeSafe -Path $installRoot
        Move-Item -LiteralPath $installRoot -Destination $backupRoot
        $backupRoot = Assert-HandleScopeLocalTreeSafe -Path $backupRoot
    }
    $stagingRoot = Assert-HandleScopeLocalTreeSafe -Path $stagingRoot
    Move-Item -LiteralPath $stagingRoot -Destination $installRoot
    $installRoot = Assert-HandleScopeLocalTreeSafe -Path $installRoot
}
catch {
    if (-not (Test-Path -LiteralPath $installRoot) -and
        (Test-Path -LiteralPath $backupRoot)) {
        $backupRoot = Assert-HandleScopeLocalTreeSafe -Path $backupRoot
        Move-Item -LiteralPath $backupRoot -Destination $installRoot
    }
    throw
}
finally {
    Remove-HandleScopeLocalItem `
        -Path $stagingRoot `
        -Recurse `
        -IgnoreMissing
}

Remove-HandleScopeLocalItem `
    -Path $backupRoot `
    -Recurse `
    -IgnoreMissing

if ($EnableAutostart) {
    $taskIdentity = Get-HandleScopeTaskIdentity
    $existingTask = Get-ScheduledTask `
        -TaskName $taskIdentity.TaskName `
        -TaskPath $taskIdentity.TaskPath `
        -ErrorAction SilentlyContinue
    if ($null -ne $existingTask) {
        if ($existingTask.Actions.Count -ne 1) {
            throw 'An unexpected scheduled task already occupies the HandleScope per-user task path.'
        }
        $expectedExecutable = [IO.Path]::GetFullPath($installedExecutable)
        $actualExecutable = [IO.Path]::GetFullPath(
            [Environment]::ExpandEnvironmentVariables(
                [string]$existingTask.Actions[0].Execute))
        if ($actualExecutable -cne $expectedExecutable -or
            -not [string]::IsNullOrEmpty([string]$existingTask.Actions[0].Arguments) -or
            $existingTask.Principal.RunLevel -ne 'Limited') {
            throw 'An unexpected scheduled task already occupies the HandleScope per-user task path.'
        }
    }

    $action = New-ScheduledTaskAction `
        -Execute $installedExecutable `
        -WorkingDirectory $installRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskIdentity.Name
    $settings = New-ScheduledTaskSettingsSet `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal `
        -UserId $taskIdentity.Name `
        -LogonType Interactive `
        -RunLevel Limited
    Register-ScheduledTask `
        -TaskName $taskIdentity.TaskName `
        -TaskPath $taskIdentity.TaskPath `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description 'Runs the restricted HandleScope Roblox automation API for this user.' `
        -Force | Out-Null
    Write-Host 'Per-user standard-privilege autostart enabled.'
}

if ($StartNow) {
    & (Join-Path $installRoot 'Start-HandleScopeApi.ps1')
}

Write-Host "HandleScope API installed for the current user at $installRoot"
if (-not $EnableAutostart) {
    Write-Host 'Autostart remains disabled. Re-run with -EnableAutostart to opt in.'
}
