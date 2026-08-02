[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Runtime = 'win-x64',

    [string]$OutputDirectory = 'artifacts\release-input',

    [switch]$SkipValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts'))
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release staging must stay beneath $artifactsRoot."
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Copy-ReviewedFile {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Reviewed release input is missing: $Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrWhiteSpace($destinationParent)) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
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

function Assert-VersionNeutralVerificationGuide {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $guide = [IO.File]::ReadAllText($Path)
    if ($guide -match '(?i)(?:HandleScope-|verify-asset\s+v)\d+\.\d+\.\d+') {
        throw 'Download verification guide contains a hard-coded release version.'
    }
    foreach ($requiredControl in @(
            '$version = $Matches.version',
            '$assetBaseName = "HandleScope-$version-win-x64"',
            'gh attestation verify $zip.FullName',
            'gh release verify-asset $tag $zip.FullName')) {
        if ($guide.IndexOf(
                $requiredControl,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Download verification guide is missing its version-neutral control: $requiredControl"
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
    Remove-Item -LiteralPath $Path -Recurse -Force
}

if ($Runtime -cne 'win-x64') {
    throw "Only the reviewed win-x64 release runtime is supported; received '$Runtime'."
}

Push-Location $repositoryRoot
try {
    if (-not $SkipValidation) {
        & (Join-Path $PSScriptRoot 'Verify-Release.ps1') `
            -Tag "v$Version" `
            -RequireCleanWorkingTree
        & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -CI
    }

    if (Test-Path -LiteralPath $outputRoot) {
        Remove-ReviewedArtifactDirectory -Path $outputRoot
    }

    $bundleRoot = Join-Path $outputRoot 'bundle'
    $desktopRoot = Join-Path $bundleRoot 'desktop'
    $apiRoot = Join-Path $bundleRoot 'api'
    $documentationRoot = Join-Path $bundleRoot 'docs'
    New-Item -ItemType Directory -Path $desktopRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $apiRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $documentationRoot -Force | Out-Null

    $desktopProject = Join-Path $repositoryRoot 'HandleScope\HandleScope.csproj'
    $apiProject = Join-Path $repositoryRoot 'HandleScope.Api\HandleScope.Api.csproj'
    foreach ($project in @($desktopProject, $apiProject)) {
        Invoke-DotNet -Arguments @(
            'restore',
            $project,
            '--runtime', $Runtime,
            '--locked-mode',
            '--nologo'
        )
    }

    $commonPublishArguments = @(
        '--configuration', 'Release',
        '--runtime', $Runtime,
        '--self-contained', 'true',
        '--no-restore',
        '--nologo',
        "-p:Version=$Version",
        '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:SatelliteResourceLanguages=en-US',
        '-p:PublishTrimmed=false',
        '-p:UseAppHost=true'
    )

    $desktopPublishArguments = @('publish', $desktopProject) +
        $commonPublishArguments + @('--output', $desktopRoot)
    $apiPublishArguments = @('publish', $apiProject) +
        $commonPublishArguments + @(
            '-p:IsTransformWebConfigDisabled=true',
            '-p:StaticWebAssetsEnabled=false',
            '--output', $apiRoot
        )
    Invoke-DotNet -Arguments $desktopPublishArguments
    Invoke-DotNet -Arguments $apiPublishArguments

    $expectedPublishedFiles = @(
        (Join-Path $desktopRoot 'HandleScope.exe'),
        (Join-Path $apiRoot 'HandleScope.Api.exe')
    )
    foreach ($expectedPublishedFile in $expectedPublishedFiles) {
        if (-not (Test-Path -LiteralPath $expectedPublishedFile -PathType Leaf)) {
            throw "Single-file release output is missing: $expectedPublishedFile"
        }
    }
    $actualPublishedFiles = @(
        Get-ChildItem -LiteralPath $desktopRoot, $apiRoot -File
    )
    if ($actualPublishedFiles.Count -ne $expectedPublishedFiles.Count) {
        throw "Single-file publish produced unexpected loose files: $($actualPublishedFiles.FullName -join ', ')"
    }

    $dependencyLibraries = [Collections.Generic.SortedDictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $dependencyManifests = @(
        (Join-Path $repositoryRoot 'HandleScope\bin\Release\net10.0-windows\win-x64\HandleScope.deps.json'),
        (Join-Path $repositoryRoot 'HandleScope.Api\bin\Release\net10.0-windows\win-x64\HandleScope.Api.deps.json')
    )
    foreach ($dependencyFile in $dependencyManifests) {
        if (-not (Test-Path -LiteralPath $dependencyFile -PathType Leaf)) {
            throw "Build dependency manifest is missing: $dependencyFile"
        }
        $dependencyManifest = Get-Content -LiteralPath $dependencyFile -Raw | ConvertFrom-Json
        foreach ($library in $dependencyManifest.libraries.PSObject.Properties) {
            $separator = $library.Name.LastIndexOf('/')
            if ($separator -le 0 -or $separator -eq $library.Name.Length - 1) {
                throw "Build dependency identity is invalid: $($library.Name)"
            }
            $libraryName = $library.Name.Substring(0, $separator)
            $libraryVersion = $library.Name.Substring($separator + 1)
            if ($libraryName -in @('HandleScope', 'HandleScope.Api', 'HandleScope.Core')) {
                continue
            }
            if (-not $libraryName.StartsWith(
                    'runtimepack.Microsoft.',
                    [StringComparison]::Ordinal)) {
                throw "Unexpected non-runtime dependency in self-contained release: $libraryName"
            }
            if ($dependencyLibraries.ContainsKey($libraryName) -and
                $dependencyLibraries[$libraryName] -cne $libraryVersion) {
                throw "Build dependency versions disagree for $libraryName."
            }
            $dependencyLibraries[$libraryName] = $libraryVersion
        }
    }
    if ($dependencyLibraries.Count -eq 0) {
        throw 'Self-contained release dependency manifests contain no runtime components.'
    }
    $runtimeComponents = [ordered]@{
        schemaVersion = 1
        runtime = $Runtime
        components = @(
            $dependencyLibraries.GetEnumerator() |
                ForEach-Object {
                    [ordered]@{
                        name = $_.Key
                        version = $_.Value
                    }
                }
        )
    }
    Write-Utf8NoBom `
        -Path (Join-Path $documentationRoot 'RUNTIME_COMPONENTS.json') `
        -Value (($runtimeComponents | ConvertTo-Json -Depth 4) + "`n")

    $debugFiles = @(
        Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
            Where-Object { $_.Extension -in @('.pdb', '.dbg') }
    )
    if ($debugFiles.Count -ne 0) {
        throw "Release staging contains debug files: $($debugFiles.FullName -join ', ')"
    }

    $sourceRevision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the source revision for release metadata.'
    }
    $sourceTimestampText = (& git show -s --format=%cI HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the source timestamp for release metadata.'
    }
    $sourceTimestampValue = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $sourceTimestampText,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$sourceTimestampValue)) {
        throw 'The source timestamp is not a valid Git commit timestamp.'
    }
    $sourceTimestamp = $sourceTimestampValue.ToUniversalTime().ToString(
        'O',
        [Globalization.CultureInfo]::InvariantCulture)

    $metadata = [ordered]@{
        schemaVersion = 1
        product = 'HandleScope'
        repository = 'Makmatoe/HandleScope'
        version = $Version
        tag = "v$Version"
        runtime = $Runtime
        sourceRevision = $sourceRevision
        sourceTimestamp = $sourceTimestamp
    }
    $runtimeManifest = [ordered]@{
        schemaVersion = 1
        product = 'HandleScope.Api'
        repository = 'Makmatoe/HandleScope'
        version = $Version
        tag = "v$Version"
        sourceRevision = $sourceRevision
        sourceTimestamp = $sourceTimestamp
        runtime = $Runtime
        discoveryApiVersion = 'v1'
        supportedApiVersions = @('v1', 'v2')
        preferredApiVersion = 'v2'
        policies = @('roblox-singleton-event-v1')
        capabilities = @(
            'handlescope.http.v1',
            'handlescope.http.v2',
            'handlescope.plan.single-use.v1',
            'handlescope.policy.roblox-singleton-event.v1'
        )
    }
    Write-Utf8NoBom `
        -Path (Join-Path $apiRoot 'HandleScope.runtime.json') `
        -Value (($runtimeManifest | ConvertTo-Json -Depth 5) + "`n")

    $lifecycleScripts = @(
        'Enable-SessionDockIntegration.ps1',
        'HandleScope.ScriptCommon.ps1',
        'Install-HandleScopeApi.ps1',
        'Invoke-HandleScopeClose.ps1',
        'Start-HandleScopeApi.ps1',
        'Stop-HandleScopeApi.ps1',
        'Uninstall-HandleScopeApi.ps1'
    )
    $sourceScriptRoot = Join-Path $repositoryRoot 'HandleScope.Api\Scripts'
    foreach ($scriptName in $lifecycleScripts) {
        Copy-ReviewedFile `
            -Source (Join-Path $sourceScriptRoot $scriptName) `
            -Destination (Join-Path $apiRoot $scriptName)
    }
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot 'API.md') `
        -Destination (Join-Path $apiRoot 'API.md')

    $rootDocuments = @(
        'README.md',
        'LICENSE.md',
        'SECURITY.md',
        'PRIVACY.md',
        'THIRD_PARTY_NOTICES.md'
    )
    foreach ($document in $rootDocuments) {
        Copy-ReviewedFile `
            -Source (Join-Path $repositoryRoot $document) `
            -Destination (Join-Path $bundleRoot $document)
    }
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot "ReleaseNotes\$Version.md") `
        -Destination (Join-Path $bundleRoot 'RELEASE_NOTES.md')
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot 'API.md') `
        -Destination (Join-Path $documentationRoot 'API.md')
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot 'docs\INSTALL.md') `
        -Destination (Join-Path $documentationRoot 'INSTALL.md')
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot 'docs\THREAT_MODEL.md') `
        -Destination (Join-Path $documentationRoot 'THREAT_MODEL.md')
    $verificationGuideSource = Join-Path $repositoryRoot 'docs\VERIFY_DOWNLOAD.md'
    Assert-VersionNeutralVerificationGuide -Path $verificationGuideSource
    Copy-ReviewedFile `
        -Source $verificationGuideSource `
        -Destination (Join-Path $documentationRoot 'VERIFY_DOWNLOAD.md')
    Copy-ReviewedFile `
        -Source (Join-Path $repositoryRoot 'docs\integrations\sessiondock.md') `
        -Destination (Join-Path $documentationRoot 'SESSIONDOCK.md')

    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
    Copy-ReviewedFile `
        -Source (Join-Path $dotnetRoot 'LICENSE.txt') `
        -Destination (Join-Path $documentationRoot 'dotnet-LICENSE.txt')
    Copy-ReviewedFile `
        -Source (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') `
        -Destination (Join-Path $documentationRoot 'dotnet-THIRD-PARTY-NOTICES.txt')

    $expectedFirstPartyFiles = @(
        'bundle/desktop/HandleScope.exe',
        'bundle/api/HandleScope.Api.exe',
        'bundle/api/HandleScope.runtime.json'
    ) + @(
        $lifecycleScripts |
            ForEach-Object { "bundle/api/$_" }
    )
    foreach ($relativePath in $expectedFirstPartyFiles) {
        $nativePath = Join-Path $outputRoot ($relativePath.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
            throw "Expected first-party release file is missing: $relativePath"
        }
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
    $bundleDifference = @(
        Compare-Object $expectedBundleFiles $actualBundleFiles -CaseSensitive
    )
    if ($bundleDifference.Count -ne 0 -or
        $expectedBundleFiles.Count -ne $actualBundleFiles.Count) {
        throw 'Release staging does not exactly match the reviewed bundle allowlist.'
    }

    Write-Utf8NoBom `
        -Path (Join-Path $outputRoot 'first-party-catalog.txt') `
        -Value (($expectedFirstPartyFiles -join "`n") + "`n")

    Write-Utf8NoBom `
        -Path (Join-Path $outputRoot 'release-metadata.json') `
        -Value (($metadata | ConvertTo-Json -Depth 4) + "`n")

    Write-Host "Reviewed release input staged at $outputRoot."
    Write-Host 'Finalize and verify this input before distributing the resulting release assets.'
}
finally {
    Pop-Location
}
