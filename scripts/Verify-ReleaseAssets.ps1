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

function Get-CanonicalJsonUtcTimestamp {
    param(
        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$PropertyName,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $pattern = '(?m)^\s*"' + [regex]::Escape($PropertyName) +
        '"\s*:\s*"(?<value>[^"\\\r\n]+)"\s*,?\s*$'
    $matches = [regex]::Matches($Json, $pattern)
    if ($matches.Count -ne 1) {
        throw "$Description must contain exactly one unescaped JSON timestamp string."
    }
    $value = $matches[0].Groups['value'].Value
    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
            $value,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$timestamp) -or
        $timestamp.Offset -ne [TimeSpan]::Zero -or
        $value -cne $timestamp.ToUniversalTime().ToString(
            'O',
            [Globalization.CultureInfo]::InvariantCulture)) {
        throw "$Description timestamp is not canonical UTC round-trip text."
    }
    return $value
}

function Assert-WindowsX64Pe {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 70) {
            throw "$Description is not a complete Windows PE file."
        }
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "$Description has no DOS/PE header."
            }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 6) {
                throw "$Description has an unsafe PE header offset."
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or
                $reader.ReadUInt16() -ne 0x8664) {
                throw "$Description is not a Windows x64 PE file."
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$releaseDirectory = [IO.Path]::GetFullPath($Directory)
if (-not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    throw "Release directory not found: $releaseDirectory"
}

$assetBaseName = "HandleScope-$Version-win-x64"
$zipName = "$assetBaseName.zip"
$sbomName = "$assetBaseName.spdx.json"
$releaseManifestName = "$assetBaseName.release.json"
$checksumName = 'SHA256SUMS.txt'
$expectedAssetNames = @(
    $zipName,
    $sbomName,
    $releaseManifestName,
    $checksumName
)
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
$releaseManifestPath = Join-Path $releaseDirectory $releaseManifestName
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
$expectedChecksummedAssets = @($zipName, $sbomName, $releaseManifestName)
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
        'api/HandleScope.Setup.exe',
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
    foreach ($executable in @(
            'desktop\HandleScope.exe',
            'api\HandleScope.Api.exe',
            'api\HandleScope.Setup.exe')) {
        Assert-WindowsX64Pe `
            -Path (Join-Path $bundleRoot $executable) `
            -Description $executable
    }

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

    $expectedApiVersions = @('v1', 'v2')
    $expectedPolicies = @('roblox-singleton-event-v1')
    $expectedCapabilities = @(
        'handlescope.http.v1',
        'handlescope.http.v2',
        'handlescope.plan.single-use.v1',
        'handlescope.policy.roblox-singleton-event.v1',
        'handlescope.setup.native.v1'
    )
    $runtimeManifestPath = Join-Path $bundleRoot 'api\HandleScope.runtime.json'
    $runtimeManifestJson = [IO.File]::ReadAllText($runtimeManifestPath)
    $runtimeSourceTimestampText = Get-CanonicalJsonUtcTimestamp `
        -Json $runtimeManifestJson `
        -PropertyName 'sourceTimestamp' `
        -Description 'Installed runtime manifest'
    $runtimeManifest = $runtimeManifestJson | ConvertFrom-Json
    Assert-ExactList `
        -Expected @(
            'schemaVersion', 'product', 'repository', 'version', 'tag',
            'sourceRevision', 'sourceTimestamp', 'runtime',
            'discoveryApiVersion', 'supportedApiVersions',
            'preferredApiVersion', 'policies', 'capabilities'
        ) `
        -Actual @($runtimeManifest.PSObject.Properties.Name) `
        -Description 'Installed runtime manifest fields'
    if ([int]$runtimeManifest.schemaVersion -ne 2) {
        throw 'Installed runtime manifest schema version is invalid.'
    }
    $expectedRuntimeIdentity = [ordered]@{
        product = 'HandleScope.Api'
        repository = 'Makmatoe/HandleScope'
        version = $Version
        tag = "v$Version"
        runtime = 'win-x64'
        discoveryApiVersion = 'v1'
        preferredApiVersion = 'v2'
    }
    foreach ($identityField in $expectedRuntimeIdentity.Keys) {
        $actualIdentityValue = [string]($runtimeManifest.$identityField)
        if ($actualIdentityValue -cne $expectedRuntimeIdentity[$identityField]) {
            throw "Installed runtime manifest identity field is invalid: $identityField"
        }
    }
    $runtimeSourceRevision = [string]($runtimeManifest.sourceRevision)
    if ($runtimeSourceRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Installed runtime manifest source revision is invalid.'
    }
    $runtimeSourceTimestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
            $runtimeSourceTimestampText,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$runtimeSourceTimestamp)) {
        throw 'Installed runtime manifest source timestamp is invalid.'
    }
    Assert-ExactList `
        -Expected $expectedApiVersions `
        -Actual @($runtimeManifest.supportedApiVersions) `
        -Description 'Installed runtime API versions'
    Assert-ExactList `
        -Expected $expectedPolicies `
        -Actual @($runtimeManifest.policies) `
        -Description 'Installed runtime policies'
    Assert-ExactList `
        -Expected $expectedCapabilities `
        -Actual @($runtimeManifest.capabilities) `
        -Description 'Installed runtime capabilities'

    $releaseManifestJson = [IO.File]::ReadAllText($releaseManifestPath)
    $releaseSourceTimestampText = Get-CanonicalJsonUtcTimestamp `
        -Json $releaseManifestJson `
        -PropertyName 'sourceTimestamp' `
        -Description 'External release manifest'
    $releaseManifest = $releaseManifestJson | ConvertFrom-Json
    Assert-ExactList `
        -Expected @(
            'schemaVersion', 'product', 'repository', 'version', 'tag',
            'runtime', 'sourceRevision', 'sourceTimestamp',
            'discoveryApiVersion', 'supportedApiVersions',
            'preferredApiVersion', 'policies', 'capabilities',
            'package', 'sbom', 'apiExecutable', 'setupExecutable'
        ) `
        -Actual @($releaseManifest.PSObject.Properties.Name) `
        -Description 'External release manifest fields'
    if ($releaseManifest.schemaVersion -ne 2 -or
        $releaseManifest.product -cne 'HandleScope' -or
        $releaseManifest.repository -cne 'Makmatoe/HandleScope' -or
        $releaseManifest.version -cne $Version -or
        $releaseManifest.tag -cne "v$Version" -or
        $releaseManifest.runtime -cne 'win-x64' -or
        $releaseManifest.discoveryApiVersion -cne 'v1' -or
        $releaseManifest.preferredApiVersion -cne 'v2' -or
        $releaseManifest.sourceRevision -cne $runtimeManifest.sourceRevision -or
        $releaseSourceTimestampText -cne $runtimeSourceTimestampText) {
        throw 'External release manifest identity is invalid.'
    }
    Assert-ExactList `
        -Expected $expectedApiVersions `
        -Actual @($releaseManifest.supportedApiVersions) `
        -Description 'External release API versions'
    Assert-ExactList `
        -Expected $expectedPolicies `
        -Actual @($releaseManifest.policies) `
        -Description 'External release policies'
    Assert-ExactList `
        -Expected $expectedCapabilities `
        -Actual @($releaseManifest.capabilities) `
        -Description 'External release capabilities'
    $assetRecords = @(
        [pscustomobject]@{
            Record = $releaseManifest.package
            Name = $zipName
            Path = $zipPath
        }
        [pscustomobject]@{
            Record = $releaseManifest.sbom
            Name = $sbomName
            Path = $sbomPath
        }
    )
    foreach ($assetRecord in $assetRecords) {
        $record = $assetRecord.Record
        $name = [string]$assetRecord.Name
        $path = [string]$assetRecord.Path
        Assert-ExactList `
            -Expected @('name', 'size', 'sha256') `
            -Actual @($record.PSObject.Properties.Name) `
            -Description "External release $name fields"
        if ($record.name -cne $name -or
            [long]$record.size -ne [IO.FileInfo]::new($path).Length -or
            $record.sha256 -cne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "External release manifest mismatch: $name"
        }
    }
    $apiExecutablePath = Join-Path $bundleRoot 'api\HandleScope.Api.exe'
    Assert-ExactList `
        -Expected @('path', 'size', 'sha256') `
        -Actual @($releaseManifest.apiExecutable.PSObject.Properties.Name) `
        -Description 'External release API executable fields'
    if ($releaseManifest.apiExecutable.path -cne 'api/HandleScope.Api.exe' -or
        [long]$releaseManifest.apiExecutable.size -ne
            [IO.FileInfo]::new($apiExecutablePath).Length -or
        $releaseManifest.apiExecutable.sha256 -cne
            (Get-FileHash -LiteralPath $apiExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'External release API executable identity is invalid.'
    }
    $setupExecutablePath = Join-Path $bundleRoot 'api\HandleScope.Setup.exe'
    Assert-ExactList `
        -Expected @('path', 'size', 'sha256') `
        -Actual @($releaseManifest.setupExecutable.PSObject.Properties.Name) `
        -Description 'External release setup executable fields'
    if ($releaseManifest.setupExecutable.path -cne 'api/HandleScope.Setup.exe' -or
        [long]$releaseManifest.setupExecutable.size -ne
            [IO.FileInfo]::new($setupExecutablePath).Length -or
        $releaseManifest.setupExecutable.sha256 -cne
            (Get-FileHash -LiteralPath $setupExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'External release setup executable identity is invalid.'
    }

    $approvedLicenseSha256 =
        'D160D2DF3EC45BBC238C19675D5D7C83086D4FB516B4A57647FBA85381465354'
    $bundleLicenseSha256 = (Get-FileHash `
            -LiteralPath (Join-Path $bundleRoot 'LICENSE.md') `
            -Algorithm SHA256).Hash
    if ($bundleLicenseSha256 -cne $approvedLicenseSha256) {
        throw 'Release bundle license does not match the reviewed MIT license text.'
    }

    $verificationGuidePath = Join-Path $bundleRoot 'docs\VERIFY_DOWNLOAD.md'
    $verificationGuide = [IO.File]::ReadAllText($verificationGuidePath)
    if ($verificationGuide -match
        '(?i)(?:HandleScope-|verify-asset\s+v)\d+\.\d+\.\d+') {
        throw 'Download verification guide contains a hard-coded release version.'
    }
    $requiredGuideControls = @(
        '$version = $Matches.version',
        '$assetBaseName = "HandleScope-$version-win-x64"',
        'gh attestation verify $zip.FullName',
        'gh release verify-asset $tag $zip.FullName',
        'HandleScope.Setup.exe',
        '& $setup verify'
    )
    foreach ($guideControl in $requiredGuideControls) {
        if ($verificationGuide.IndexOf(
                $guideControl,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Download verification guide is missing its version-neutral control: $guideControl"
        }
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
