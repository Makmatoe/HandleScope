[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

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

$releaseDirectory = [IO.Path]::GetFullPath($Directory)
if (-not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    throw "Release directory not found: $releaseDirectory"
}

$assetBaseName = "HandleScope-$Version-win-x64"
$zipName = "$assetBaseName.zip"
$sbomName = "$assetBaseName.spdx.json"
$checksumName = 'SHA256SUMS.txt'
$expectedAssetNames = @($zipName, $sbomName, $checksumName)
$actualAssetNames = @(
    Get-ChildItem -LiteralPath $releaseDirectory -File |
        Select-Object -ExpandProperty Name
)
Assert-ExactList `
    -Expected $expectedAssetNames `
    -Actual $actualAssetNames `
    -Description 'Release asset list'

$zipPath = Join-Path $releaseDirectory $zipName
$sbomPath = Join-Path $releaseDirectory $sbomName
$checksumPath = Join-Path $releaseDirectory $checksumName
$checksumEntries = [ordered]@{}
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[A-Za-z0-9][A-Za-z0-9._-]*)$') {
        throw "Invalid SHA256SUMS entry: $line"
    }
    $name = $Matches.name
    if ($checksumEntries.Contains($name)) {
        throw "Duplicate SHA256SUMS entry: $name"
    }
    $checksumEntries[$name] = $Matches.hash
}
$expectedChecksummedAssets = @($zipName, $sbomName)
Assert-ExactList `
    -Expected $expectedChecksummedAssets `
    -Actual @($checksumEntries.Keys) `
    -Description 'SHA256SUMS file list'
foreach ($name in $expectedChecksummedAssets) {
    $actualHash = (Get-FileHash `
        -LiteralPath (Join-Path $releaseDirectory $name) `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($checksumEntries[$name] -cne $actualHash) {
        throw "SHA-256 mismatch for $name."
    }
}

$temporaryRoot = [IO.Path]::GetFullPath((Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("handlescope-release-verification-" + [Guid]::NewGuid().ToString('N'))))
$temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
if (-not $temporaryRoot.StartsWith(
        $temporaryPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporary extraction path is outside the operating-system temporary directory.'
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 10000) {
            throw 'Release ZIP entry count is outside the reviewed limit.'
        }

        $seenEntries = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        [long]$totalLength = 0
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                $entryName.StartsWith('/', [StringComparison]::Ordinal) -or
                $entryName -notmatch "^$([regex]::Escape($assetBaseName))/" -or
                $entryName.Split('/') -contains '..') {
                throw "Release ZIP contains an unsafe entry name: $entryName"
            }
            $pathSegments = @($entryName.Split('/'))
            for ($segmentIndex = 0; $segmentIndex -lt $pathSegments.Count; $segmentIndex++) {
                $segment = $pathSegments[$segmentIndex]
                $isTrailingDirectoryMarker =
                    $segmentIndex -eq $pathSegments.Count - 1 -and
                    [string]::IsNullOrEmpty($segment)
                if ($isTrailingDirectoryMarker) {
                    continue
                }
                $deviceStem = $segment.Split('.')[0]
                if ([string]::IsNullOrEmpty($segment) -or
                    $segment -in @('.', '..') -or
                    $segment -match '[<>:"|?*\x00-\x1f]' -or
                    $segment.EndsWith('.', [StringComparison]::Ordinal) -or
                    $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
                    $deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                    throw "Release ZIP contains an unsafe Windows path segment: $entryName"
                }
            }
            if (-not $seenEntries.Add($entryName)) {
                throw "Release ZIP contains a duplicate case-insensitive entry: $entryName"
            }
            if ($entry.Length -gt 536870912) {
                throw "Release ZIP entry exceeds 512 MiB: $entryName"
            }
            $totalLength += $entry.Length
            if ($totalLength -gt 1073741824) {
                throw 'Release ZIP expands beyond the reviewed 1 GiB limit.'
            }

            $destination = [IO.Path]::GetFullPath((Join-Path $temporaryRoot $entryName))
            $extractionPrefix = $temporaryRoot.TrimEnd('\', '/') +
                [IO.Path]::DirectorySeparatorChar
            if (-not $destination.StartsWith(
                    $extractionPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Release ZIP entry escapes the extraction directory: $entryName"
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                continue
            }

            $destinationParent = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
            $sourceStream = $entry.Open()
            $destinationStream = [IO.File]::Open(
                $destination,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $sourceStream.CopyTo($destinationStream)
            }
            finally {
                $destinationStream.Dispose()
                $sourceStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $bundleRoot = Join-Path $temporaryRoot $assetBaseName
    if (-not (Test-Path -LiteralPath $bundleRoot -PathType Container)) {
        throw 'Release ZIP does not contain the expected single top-level directory.'
    }
    $extractedTopLevelItems = @(Get-ChildItem -LiteralPath $temporaryRoot -Force)
    if ($extractedTopLevelItems.Count -ne 1 -or
        $extractedTopLevelItems[0].Name -cne $assetBaseName) {
        throw 'Release ZIP contains unexpected top-level items.'
    }

    $expectedBundleFiles = @(
        'CONTENTS.sha256',
        'LICENSE.md',
        'PRIVACY.md',
        'README.md',
        'RELEASE_NOTES.md',
        'SECURITY.md',
        'THIRD_PARTY_NOTICES.md',
        'api/API.md',
        'api/HandleScope.Api.exe',
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

    $installerPath = Join-Path $bundleRoot 'api\Install-HandleScopeApi.ps1'
    & $installerPath -VerifyOnly

    $contentsManifestPath = Join-Path $bundleRoot 'CONTENTS.sha256'
    if (-not (Test-Path -LiteralPath $contentsManifestPath -PathType Leaf)) {
        throw 'Release bundle has no CONTENTS.sha256 manifest.'
    }
    $manifestEntries = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $contentsManifestPath) {
        if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<path>[^\\\r\n]+)$') {
            throw "Invalid CONTENTS.sha256 entry: $line"
        }
        $relativePath = $Matches.path
        if ([IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Split('/') -contains '..' -or
            $relativePath -ceq 'CONTENTS.sha256') {
            throw "Unsafe CONTENTS.sha256 path: $relativePath"
        }
        if ($manifestEntries.Contains($relativePath)) {
            throw "Duplicate CONTENTS.sha256 path: $relativePath"
        }
        $manifestEntries[$relativePath] = $Matches.hash
    }

    $actualManifestFiles = @(
        Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
            ForEach-Object {
                Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName
            } |
            Where-Object { $_ -cne 'CONTENTS.sha256' }
    )
    Assert-ExactList `
        -Expected @($manifestEntries.Keys) `
        -Actual $actualManifestFiles `
        -Description 'Bundle contents manifest'
    foreach ($relativePath in $manifestEntries.Keys) {
        $filePath = Join-Path $bundleRoot ($relativePath.Replace('/', '\'))
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($manifestEntries[$relativePath] -cne $actualHash) {
            throw "Bundle content hash mismatch: $relativePath"
        }
    }

    $approvedLicenseSha256 =
        'D160D2DF3EC45BBC238C19675D5D7C83086D4FB516B4A57647FBA85381465354'
    $bundleLicenseSha256 = (Get-FileHash `
            -LiteralPath (Join-Path $bundleRoot 'LICENSE.md') `
            -Algorithm SHA256).Hash
    if ($bundleLicenseSha256 -cne $approvedLicenseSha256) {
        throw 'Release bundle license does not match the reviewed MIT license text.'
    }

    $bundleFiles = @(
        Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
            Sort-Object {
                Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName
            }
    )
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    if ($sbom.spdxVersion -cne 'SPDX-2.3' -or
        $sbom.dataLicense -cne 'CC0-1.0' -or
        $sbom.SPDXID -cne 'SPDXRef-DOCUMENT' -or
        $sbom.name -cne $assetBaseName -or
        $sbom.documentNamespace -cne "https://github.com/Makmatoe/HandleScope/releases/download/v$Version/$sbomName") {
        throw 'SPDX document identity is invalid.'
    }
    $packages = @($sbom.packages)
    $rootPackages = @(
        $packages |
            Where-Object { $_.SPDXID -ceq 'SPDXRef-Package-HandleScope' }
    )
    if ($rootPackages.Count -ne 1 -or
        $rootPackages[0].versionInfo -cne $Version -or
        $rootPackages[0].filesAnalyzed -ne $true -or
        $rootPackages[0].licenseConcluded -cne 'MIT' -or
        $rootPackages[0].licenseDeclared -cne 'MIT') {
        throw 'SPDX package identity is invalid.'
    }
    $rootPackage = $rootPackages[0]

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
    if ($packages.Count -ne $dependencyLibraries.Count + 1) {
        throw 'SPDX dependency package count does not match published dependency manifests.'
    }
    $dependencyIndex = 0
    foreach ($dependency in $dependencyLibraries.GetEnumerator()) {
        $dependencyIndex++
        $expectedSpdxId = 'SPDXRef-Package-Dependency-{0:D3}' -f $dependencyIndex
        $records = @($packages | Where-Object { $_.SPDXID -ceq $expectedSpdxId })
        if ($records.Count -ne 1 -or
            $records[0].name -cne $dependency.Key -or
            $records[0].versionInfo -cne $dependency.Value -or
            $records[0].filesAnalyzed -ne $false) {
            throw "SPDX dependency record is invalid: $($dependency.Key)"
        }
        $externalReferences = @($records[0].externalRefs)
        $expectedPurl = "pkg:nuget/$([Uri]::EscapeDataString($dependency.Key))@$([Uri]::EscapeDataString($dependency.Value))"
        if ($externalReferences.Count -ne 1 -or
            $externalReferences[0].referenceCategory -cne 'PACKAGE-MANAGER' -or
            $externalReferences[0].referenceType -cne 'purl' -or
            $externalReferences[0].referenceLocator -cne $expectedPurl) {
            throw "SPDX package URL is invalid: $($dependency.Key)"
        }
    }
    $sbomFiles = @($sbom.files)
    if ($sbomFiles.Count -ne $bundleFiles.Count) {
        throw 'SPDX file count does not match the release bundle.'
    }
    $sbomPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    for ($index = 0; $index -lt $sbomFiles.Count; $index++) {
        $fileRecord = $sbomFiles[$index]
        $expectedSpdxId = 'SPDXRef-File-{0:D5}' -f ($index + 1)
        if ($fileRecord.SPDXID -cne $expectedSpdxId -or
            [string]$fileRecord.fileName -cnotmatch '^\./') {
            throw 'SPDX file identifier or path is invalid.'
        }
        $relativePath = ([string]$fileRecord.fileName).Substring(2)
        if (-not $sbomPaths.Add($relativePath)) {
            throw "SPDX contains a duplicate path: $relativePath"
        }
        $filePath = Join-Path $bundleRoot ($relativePath.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "SPDX references a missing bundle file: $relativePath"
        }
        $checksums = @($fileRecord.checksums)
        if ($checksums.Count -ne 1 -or $checksums[0].algorithm -cne 'SHA256') {
            throw "SPDX checksum declaration is invalid: $relativePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($checksums[0].checksumValue -cne $actualHash) {
            throw "SPDX checksum mismatch: $relativePath"
        }
    }

    $actualBundlePaths = @(
        $bundleFiles |
            ForEach-Object {
                Get-RelativeSlashPath -BasePath $bundleRoot -Path $_.FullName
            }
    )
    Assert-ExactList `
        -Expected $actualBundlePaths `
        -Actual @($sbomPaths) `
        -Description 'SPDX file list'

    $sha1Hashes = @(
        $bundleFiles |
            ForEach-Object {
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
            } |
            Sort-Object
    )
    $expectedVerificationCode = Get-StringSha1 -Value ($sha1Hashes -join '')
    if ($rootPackage.packageVerificationCode.packageVerificationCodeValue -cne
        $expectedVerificationCode) {
        throw 'SPDX package verification code does not match the release bundle.'
    }

    $relationships = @($sbom.relationships)
    $containsRelationships = @(
        $relationships |
            Where-Object {
                $_.spdxElementId -ceq 'SPDXRef-Package-HandleScope' -and
                $_.relationshipType -ceq 'CONTAINS'
            }
    )
    $dependencyRelationships = @(
        $relationships |
            Where-Object {
                $_.spdxElementId -ceq 'SPDXRef-Package-HandleScope' -and
                $_.relationshipType -ceq 'DEPENDS_ON'
            }
    )
    if ($containsRelationships.Count -ne $bundleFiles.Count -or
        $dependencyRelationships.Count -ne $dependencyLibraries.Count -or
        $relationships.Count -ne $containsRelationships.Count + $dependencyRelationships.Count) {
        throw 'SPDX relationships do not exactly describe bundle files and runtime dependencies.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        if (-not $resolvedTemporaryRoot.StartsWith(
                $temporaryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove an unexpected temporary path.'
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

Write-Host "Verified HandleScope $Version release inventory, checksums, and SPDX SBOM."
