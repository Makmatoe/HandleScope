[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar

function Resolve-ArtifactPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolved = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
    }
    if (-not $resolved.StartsWith(
            $artifactsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release paths must stay beneath $artifactsRoot."
    }
    return $resolved
}

function Get-RelativeSlashPath {
    param(
        [Parameter(Mandatory)]
        [string]$BasePath,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $baseFullPath = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $targetFullPath = [IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Value
    )

    [IO.File]::WriteAllText(
        $Path,
        $Value,
        [Text.UTF8Encoding]::new($false))
}

function Get-StringSha1 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $algorithm = [Security.Cryptography.SHA1]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-ExactList {
    param(
        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string[]]$Actual,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $difference = @(
        Compare-Object `
            -ReferenceObject $Expected `
            -DifferenceObject $Actual `
            -CaseSensitive
    )
    if ($difference.Count -ne 0 -or $Expected.Count -ne $Actual.Count) {
        throw "$Description does not match the reviewed release set."
    }
}

function Test-PathNestedWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$PotentialParent
    )

    $parentPrefix = $PotentialParent.TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith(
        $parentPrefix,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ArtifactPathHasNoFileSystemLinks {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $relativePath = $Path.Substring($artifactsPrefix.Length)
    $currentPath = $artifactsRoot
    foreach ($component in $relativePath.Split(
            [IO.Path]::DirectorySeparatorChar,
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $component
        if (-not (Test-Path `
                -LiteralPath $currentPath `
                -ErrorAction Stop)) {
            break
        }

        $item = Get-Item `
            -LiteralPath $currentPath `
            -Force `
            -ErrorAction Stop
        $linkTypeProperty = $item.PSObject.Properties['LinkType']
        if ($null -ne $linkTypeProperty -and
            -not [string]::IsNullOrWhiteSpace(
                [string]$linkTypeProperty.Value)) {
            throw "Release paths must not traverse filesystem links: $currentPath"
        }
    }
}

function Remove-ReviewedArtifactDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    Assert-ArtifactPathHasNoFileSystemLinks -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Refusing to replace a non-directory release output: $Path"
    }
    $items = @(
        Get-Item -LiteralPath $Path -Force
        Get-ChildItem -LiteralPath $Path -Recurse -Force
    )
    $linkedItems = @(
        $items |
            Where-Object {
                ($null -ne $_.PSObject.Properties['LinkType']) -and
                -not [string]::IsNullOrWhiteSpace([string]$_.LinkType)
            }
    )
    if ($linkedItems.Count -ne 0) {
        throw "Refusing to remove release output containing filesystem links: $($linkedItems.FullName -join ', ')"
    }
    Assert-ArtifactPathHasNoFileSystemLinks -Path $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$inputRoot = Resolve-ArtifactPath -Path $InputDirectory
$outputRoot = Resolve-ArtifactPath -Path $OutputDirectory
Assert-ArtifactPathHasNoFileSystemLinks -Path $inputRoot
Assert-ArtifactPathHasNoFileSystemLinks -Path $outputRoot
if ([string]::Equals(
        $inputRoot,
        $outputRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathNestedWithin -Path $outputRoot -PotentialParent $inputRoot) -or
    (Test-PathNestedWithin -Path $inputRoot -PotentialParent $outputRoot)) {
    throw 'Release input and output must be separate, non-overlapping directories.'
}
if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) {
    throw "Release input was not found: $inputRoot"
}

$metadataPath = Join-Path $inputRoot 'release-metadata.json'
$catalogPath = Join-Path $inputRoot 'first-party-catalog.txt'
$bundleRoot = Join-Path $inputRoot 'bundle'
foreach ($requiredPath in @($metadataPath, $catalogPath, $bundleRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Release input is incomplete: $requiredPath"
    }
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$requiredMetadataFields = @(
    'schemaVersion',
    'product',
    'repository',
    'version',
    'tag',
    'runtime',
    'sourceRevision',
    'sourceTimestamp'
)
$actualMetadataFields = @($metadata.PSObject.Properties.Name)
$metadataDifference = @(
    Compare-Object `
        -ReferenceObject $requiredMetadataFields `
        -DifferenceObject $actualMetadataFields `
        -CaseSensitive
)
if ($metadataDifference.Count -ne 0 -or
    $actualMetadataFields.Count -ne $requiredMetadataFields.Count) {
    throw 'Release metadata contains missing or unexpected fields.'
}
if ($metadata.schemaVersion -ne 1 -or
    $metadata.product -cne 'HandleScope' -or
    $metadata.repository -cne 'Makmatoe/HandleScope' -or
    $metadata.runtime -cne 'win-x64' -or
    [string]$metadata.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $metadata.tag -cne "v$($metadata.version)" -or
    [string]$metadata.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Release metadata identity, version, runtime, or source revision is invalid.'
}
$sourceTimestamp = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse(
        [string]$metadata.sourceTimestamp,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$sourceTimestamp)) {
    throw 'Release metadata source timestamp is invalid.'
}

Push-Location $repositoryRoot
try {
    $checkedOutRevision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $checkedOutRevision -cne $metadata.sourceRevision) {
        throw 'Release input does not belong to the checked-out source revision.'
    }
}
finally {
    Pop-Location
}

$expectedPeFiles = @(
    'bundle/desktop/HandleScope.exe',
    'bundle/api/HandleScope.Api.exe'
)
$expectedScriptFiles = @(
    'bundle/api/Enable-SessionDockIntegration.ps1',
    'bundle/api/HandleScope.ScriptCommon.ps1',
    'bundle/api/Install-HandleScopeApi.ps1',
    'bundle/api/Invoke-HandleScopeClose.ps1',
    'bundle/api/Start-HandleScopeApi.ps1',
    'bundle/api/Stop-HandleScopeApi.ps1',
    'bundle/api/Uninstall-HandleScopeApi.ps1'
)
$expectedFirstPartyFiles = $expectedPeFiles + $expectedScriptFiles + @(
    'bundle/api/HandleScope.runtime.json'
)
$catalogEntries = @(
    Get-Content -LiteralPath $catalogPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$catalogDifference = @(
    Compare-Object `
        -ReferenceObject $expectedFirstPartyFiles `
        -DifferenceObject $catalogEntries `
        -CaseSensitive
)
if ($catalogDifference.Count -ne 0 -or
    $catalogEntries.Count -ne $expectedFirstPartyFiles.Count) {
    throw 'The first-party catalog does not exactly match the reviewed release allowlist.'
}

$actualHandleScopePeFiles = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
        Where-Object {
            $_.Name -like 'HandleScope*.exe' -or
            $_.Name -like 'HandleScope*.dll'
        } |
        ForEach-Object { Get-RelativeSlashPath -BasePath $inputRoot -Path $_.FullName }
)
$actualScriptFiles = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File -Filter '*.ps1' |
        ForEach-Object { Get-RelativeSlashPath -BasePath $inputRoot -Path $_.FullName }
)
Assert-ExactList `
    -Expected $expectedPeFiles `
    -Actual $actualHandleScopePeFiles `
    -Description 'First-party PE file list'
Assert-ExactList `
    -Expected $expectedScriptFiles `
    -Actual $actualScriptFiles `
    -Description 'PowerShell script file list'

$bundleItems = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -Force)
$unsafeItems = @(
    $bundleItems |
        Where-Object {
            (($null -ne $_.PSObject.Properties['LinkType']) -and
                -not [string]::IsNullOrWhiteSpace([string]$_.LinkType)) -or
            (-not $_.PSIsContainer -and $_.Extension -in @('.pdb', '.dbg', '.dmp', '.mdmp', '.etl')) -or
            (-not $_.PSIsContainer -and $_.Name -in @('connection.json', 'api.log'))
        }
)
if ($unsafeItems.Count -ne 0) {
    throw "Release input contains unsafe generated, runtime, or linked items: $($unsafeItems.FullName -join ', ')"
}

$expectedBundleFiles = @(
    'LICENSE.md',
    'PRIVACY.md',
    'README.md',
    'RELEASE_NOTES.md',
    'SECURITY.md',
    'THIRD_PARTY_NOTICES.md',
    'api/API.md',
    'api/HandleScope.Api.exe',
    'api/HandleScope.runtime.json',
        'api/Enable-SessionDockIntegration.ps1',
        'api/HandleScope.ScriptCommon.ps1',
    'api/Install-HandleScopeApi.ps1',
    'api/Invoke-HandleScopeClose.ps1',
    'api/Start-HandleScopeApi.ps1',
    'api/Stop-HandleScopeApi.ps1',
    'api/Uninstall-HandleScopeApi.ps1',
    'desktop/HandleScope.exe',
    'docs/API.md',
    'docs/INSTALL.md',
    'docs/RUNTIME_COMPONENTS.json',
    'docs/SESSIONDOCK.md',
    'docs/THREAT_MODEL.md',
    'docs/VERIFY_DOWNLOAD.md',
    'docs/dotnet-LICENSE.txt',
    'docs/dotnet-THIRD-PARTY-NOTICES.txt'
)
$actualBundleFiles = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
        ForEach-Object {
            Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName
        }
)
Assert-ExactList `
    -Expected $expectedBundleFiles `
    -Actual $actualBundleFiles `
    -Description 'Release bundle file list'

$contentsManifestPath = Join-Path $bundleRoot 'CONTENTS.sha256'
if (Test-Path -LiteralPath $contentsManifestPath) {
    Remove-Item -LiteralPath $contentsManifestPath -Force
}
$contentFiles = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
        Sort-Object { Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName }
)
$contentLines = foreach ($file in $contentFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relativePath = Get-RelativeSlashPath -BasePath $bundleRoot -Path $file.FullName
    "$hash  $relativePath"
}
Write-Utf8NoBom -Path $contentsManifestPath -Value (($contentLines -join "`n") + "`n")

$allBundleFiles = @(
    Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
        Sort-Object { Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName }
)
$sha1Hashes = @(
    $allBundleFiles |
        ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
        } |
        Sort-Object
)
$packageVerificationCode = Get-StringSha1 -Value ($sha1Hashes -join '')
$spdxFiles = [System.Collections.Generic.List[object]]::new()
$relationships = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $allBundleFiles.Count; $index++) {
    $file = $allBundleFiles[$index]
    $spdxId = 'SPDXRef-File-{0:D5}' -f ($index + 1)
    $relativePath = Get-RelativeSlashPath -BasePath $bundleRoot -Path $file.FullName
    $spdxFiles.Add([ordered]@{
        fileName = "./$relativePath"
        SPDXID = $spdxId
        checksums = @(
            [ordered]@{
                algorithm = 'SHA256'
                checksumValue = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        )
        licenseConcluded = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    })
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-HandleScope'
        relationshipType = 'CONTAINS'
        relatedSpdxElement = $spdxId
    })
}

$dependencyLibraries = [Collections.Generic.SortedDictionary[string, string]]::new(
    [StringComparer]::Ordinal)
$runtimeComponentsPath = Join-Path $bundleRoot 'docs\RUNTIME_COMPONENTS.json'
if (-not (Test-Path -LiteralPath $runtimeComponentsPath -PathType Leaf)) {
    throw 'Reviewed runtime-component manifest is missing.'
}
$runtimeComponents = Get-Content -LiteralPath $runtimeComponentsPath -Raw | ConvertFrom-Json
$runtimeFields = @($runtimeComponents.PSObject.Properties.Name)
$expectedRuntimeFields = @('schemaVersion', 'runtime', 'components')
$runtimeFieldDifference = @(
    Compare-Object $expectedRuntimeFields $runtimeFields -CaseSensitive
)
if ($runtimeFieldDifference.Count -ne 0 -or
    $runtimeFields.Count -ne $expectedRuntimeFields.Count -or
    $runtimeComponents.schemaVersion -ne 1 -or
    $runtimeComponents.runtime -cne 'win-x64') {
    throw 'Reviewed runtime-component manifest identity is invalid.'
}
foreach ($component in @($runtimeComponents.components)) {
    $componentFields = @($component.PSObject.Properties.Name)
    $expectedComponentFields = @('name', 'version')
    $componentFieldDifference = @(
        Compare-Object $expectedComponentFields $componentFields -CaseSensitive
    )
    if ($componentFieldDifference.Count -ne 0 -or
        $componentFields.Count -ne $expectedComponentFields.Count -or
        [string]$component.name -cnotmatch '^runtimepack\.Microsoft\.[A-Za-z0-9.-]+$' -or
        [string]$component.version -cnotmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$' -or
        $dependencyLibraries.ContainsKey([string]$component.name)) {
        throw 'Reviewed runtime-component manifest contains an invalid or duplicate component.'
    }
    $dependencyLibraries[[string]$component.name] = [string]$component.version
}
if ($dependencyLibraries.Count -eq 0) {
    throw 'Reviewed runtime-component manifest contains no components.'
}

$version = [string]$metadata.version
$assetBaseName = "HandleScope-$version-win-x64"
$zipName = "$assetBaseName.zip"
$sbomName = "$assetBaseName.spdx.json"
$documentNamespace = "https://github.com/Makmatoe/HandleScope/releases/download/v$version/$sbomName"
$spdxPackages = [System.Collections.Generic.List[object]]::new()
$spdxPackages.Add([ordered]@{
    name = 'HandleScope'
    SPDXID = 'SPDXRef-Package-HandleScope'
    versionInfo = $version
    downloadLocation = "https://github.com/Makmatoe/HandleScope/releases/download/v$version/$zipName"
    filesAnalyzed = $true
    packageVerificationCode = [ordered]@{
        packageVerificationCodeValue = $packageVerificationCode
    }
    licenseConcluded = 'MIT'
    licenseDeclared = 'MIT'
    copyrightText = 'Copyright (c) 2026 Makmatoe'
})
$dependencyIndex = 0
foreach ($dependency in $dependencyLibraries.GetEnumerator()) {
    $dependencyIndex++
    $dependencySpdxId = 'SPDXRef-Package-Dependency-{0:D3}' -f $dependencyIndex
    $supplier = if ($dependency.Key.StartsWith(
            'runtimepack.Microsoft.',
            [StringComparison]::Ordinal)) {
        'Organization: Microsoft Corporation'
    }
    else {
        'NOASSERTION'
    }
    $spdxPackages.Add([ordered]@{
        name = $dependency.Key
        SPDXID = $dependencySpdxId
        versionInfo = $dependency.Value
        supplier = $supplier
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
        externalRefs = @(
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$([Uri]::EscapeDataString($dependency.Key))@$([Uri]::EscapeDataString($dependency.Value))"
            }
        )
    })
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-Package-HandleScope'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $dependencySpdxId
    })
}

$spdx = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = $assetBaseName
    documentNamespace = $documentNamespace
    creationInfo = [ordered]@{
        created = $sourceTimestamp.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @(
            'Organization: Makmatoe',
            'Tool: HandleScope release pipeline'
        )
    }
    documentDescribes = @('SPDXRef-Package-HandleScope')
    packages = @($spdxPackages)
    files = @($spdxFiles)
    relationships = @($relationships)
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-ReviewedArtifactDirectory -Path $outputRoot
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$zipPath = Join-Path $outputRoot $zipName
$archiveTimestamp = $sourceTimestamp.ToUniversalTime()
if ($archiveTimestamp.Year -lt 1980) {
    $archiveTimestamp = [DateTimeOffset]::new(
        1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
}
if ($archiveTimestamp.Year -gt 2107) {
    throw 'Source timestamp cannot be represented safely in a ZIP archive.'
}

$zipStream = [IO.File]::Open(
    $zipPath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::ReadWrite,
    [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $zipStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $true,
        [Text.UTF8Encoding]::new($false))
    try {
        foreach ($file in $allBundleFiles) {
            $relativePath = Get-RelativeSlashPath -BasePath $bundleRoot -Path $file.FullName
            $entry = $archive.CreateEntry(
                "$assetBaseName/$relativePath",
                [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $archiveTimestamp
            $entryStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($file.FullName)
            try {
                $sourceStream.CopyTo($entryStream)
            }
            finally {
                $sourceStream.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $zipStream.Dispose()
}

$sbomPath = Join-Path $outputRoot $sbomName
Write-Utf8NoBom -Path $sbomPath -Value (($spdx | ConvertTo-Json -Depth 12) + "`n")
$releaseManifestName = "$assetBaseName.release.json"
$releaseManifestPath = Join-Path $outputRoot $releaseManifestName
$apiExecutablePath = Join-Path $bundleRoot 'api\HandleScope.Api.exe'
$releaseManifest = [ordered]@{
    schemaVersion = 1
    product = 'HandleScope'
    repository = 'Makmatoe/HandleScope'
    version = $version
    tag = "v$version"
    runtime = 'win-x64'
    sourceRevision = [string]$metadata.sourceRevision
    sourceTimestamp = [string]$metadata.sourceTimestamp
    discoveryApiVersion = 'v1'
    supportedApiVersions = @('v1', 'v2')
    preferredApiVersion = 'v2'
    policies = @('roblox-singleton-event-v1')
    capabilities = @(
        'handlescope.http.v1',
        'handlescope.http.v2',
        'handlescope.policy.roblox-singleton-event.v1',
        'handlescope.plan.single-use.v1'
    )
    package = [ordered]@{
        name = $zipName
        size = [IO.FileInfo]::new($zipPath).Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    sbom = [ordered]@{
        name = $sbomName
        size = [IO.FileInfo]::new($sbomPath).Length
        sha256 = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    apiExecutable = [ordered]@{
        path = 'api/HandleScope.Api.exe'
        size = [IO.FileInfo]::new($apiExecutablePath).Length
        sha256 = (Get-FileHash -LiteralPath $apiExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
Write-Utf8NoBom `
    -Path $releaseManifestPath `
    -Value (($releaseManifest | ConvertTo-Json -Depth 8) + "`n")
$checksumFiles = @($zipPath, $sbomPath, $releaseManifestPath) |
    Sort-Object { Split-Path -Leaf $_ }
$checksumLines = foreach ($file in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $file)"
}
Write-Utf8NoBom `
    -Path (Join-Path $outputRoot 'SHA256SUMS.txt') `
    -Value (($checksumLines -join "`n") + "`n")

& (Join-Path $PSScriptRoot 'Verify-ReleaseAssets.ps1') `
    -Directory $outputRoot `
    -Version $version

Write-Host "Release assets finalized and verified at $outputRoot."
