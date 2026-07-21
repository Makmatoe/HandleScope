[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$requiredPaths = @(
    '.editorconfig'
    '.gitattributes'
    '.github\dependabot.yml'
    '.github\CODEOWNERS'
    '.github\ISSUE_TEMPLATE\bug_report.md'
    '.github\ISSUE_TEMPLATE\config.yml'
    '.github\ISSUE_TEMPLATE\feature_request.md'
    '.github\pull_request_template.md'
    '.github\workflows\ci.yml'
    '.github\workflows\codeql.yml'
    '.github\workflows\release.yml'
    '.gitignore'
    'API.md'
    'CHANGELOG.md'
    'CONTRIBUTING.md'
    'Directory.Build.props'
    'docs\INSTALL.md'
    'docs\RELEASING.md'
    'docs\REPOSITORY_SETTINGS.md'
    'docs\THREAT_MODEL.md'
    'docs\VERIFY_DOWNLOAD.md'
    'docs\integrations\sessiondock.md'
    'global.json'
    'HandleScope.Api\Scripts\Enable-SessionDockIntegration.ps1'
    'HandleScope.slnx'
    'LICENSE.md'
    'NuGet.Config'
    'PRIVACY.md'
    'README.md'
    'ReleaseNotes\0.1.0.md'
    'ReleaseNotes\0.1.1.md'
    'scripts\Finalize-Release.ps1'
    'scripts\Publish-Release.ps1'
    'scripts\Test-PowerShellCompatibility.ps1'
    'scripts\Verify-Release.ps1'
    'scripts\Verify-ReleaseAssets.ps1'
    'SECURITY.md'
    'THIRD_PARTY_NOTICES.md'
)

$failures = [System.Collections.Generic.List[string]]::new()

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $basePath = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $targetPath = [IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]::new($basePath)
    $targetUri = [Uri]::new($targetPath)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($targetUri).ToString()).Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar)
}

foreach ($relativePath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath))) {
        $failures.Add("Required repository file is missing: $relativePath")
    }
}

try {
    $globalJson = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot 'global.json') `
        -Raw |
        ConvertFrom-Json
    if ([string]$globalJson.sdk.version -ne '10.0.300') {
        $failures.Add('global.json must pin the reviewed .NET SDK version 10.0.300.')
    }
    if ([string]$globalJson.sdk.rollForward -ne 'disable') {
        $failures.Add('global.json must disable SDK roll-forward.')
    }
}
catch {
    $failures.Add("global.json is not valid JSON: $($_.Exception.Message)")
}

try {
    [xml]$buildProperties = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') `
        -Raw
    $runtimeIdentifiers = @(
        $buildProperties.SelectNodes('/Project/PropertyGroup/RuntimeIdentifiers') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($runtimeIdentifiers.Count -ne 1 -or
        $runtimeIdentifiers[0] -cne 'win-x64') {
        $failures.Add('Directory.Build.props must declare only the reviewed win-x64 release runtime.')
    }
    $licenseExpressions = @(
        $buildProperties.SelectNodes('/Project/PropertyGroup/PackageLicenseExpression') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($licenseExpressions.Count -ne 1 -or
        $licenseExpressions[0] -cne 'MIT') {
        $failures.Add('Directory.Build.props must declare the MIT license expression.')
    }
}
catch {
    $failures.Add("Directory.Build.props is not valid XML: $($_.Exception.Message)")
}

$projectFiles = Get-ChildItem `
    -LiteralPath $repositoryRoot `
    -Recurse `
    -File `
    -Filter '*.csproj' |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]'
    }

foreach ($projectFile in $projectFiles) {
    $packageReference = Select-String `
        -LiteralPath $projectFile.FullName `
        -Pattern '<PackageReference\b' `
        -Quiet
    if ($packageReference) {
        $relativePath = Get-RepositoryRelativePath -Path $projectFile.FullName
        $failures.Add("Third-party PackageReference found in $relativePath.")
    }

    $lockFile = Join-Path $projectFile.DirectoryName 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
        $relativePath = Get-RepositoryRelativePath -Path $projectFile.FullName
        $failures.Add("NuGet lock file is missing beside $relativePath.")
    }
}

$excludedDirectoryPattern = '[\\/](\.git|bin|obj|artifacts|TestResults|coverage)[\\/]'
$repositoryFiles = Get-ChildItem `
    -LiteralPath $repositoryRoot `
    -Recurse `
    -File `
    -Force |
    Where-Object {
        $_.FullName -notmatch $excludedDirectoryPattern
    }

$sensitiveNames = @(
    'connection.json'
    'api.log'
    '.env'
)
$sensitiveExtensions = @(
    '.dmp'
    '.etl'
    '.key'
    '.mdmp'
    '.p12'
    '.pem'
    '.pfx'
    '.pvk'
    '.snk'
)
$generatedBinaryExtensions = @(
    '.dll'
    '.exe'
    '.msi'
    '.msix'
    '.nupkg'
    '.pdb'
    '.snupkg'
    '.zip'
)

foreach ($file in $repositoryFiles) {
    if ($file.Name -in $sensitiveNames -or
        $file.Extension -in $sensitiveExtensions -or
        $file.Name.EndsWith('.secrets.json', [StringComparison]::OrdinalIgnoreCase) -or
        $file.Name.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase)) {
        $relativePath = Get-RepositoryRelativePath -Path $file.FullName
        $failures.Add("Sensitive runtime or signing file is present: $relativePath")
    }

    if ($file.Extension -in $generatedBinaryExtensions) {
        $relativePath = Get-RepositoryRelativePath -Path $file.FullName
        $failures.Add("Generated binary or release archive is present in source: $relativePath")
    }
}

$textFileExtensions = @(
    '.config', '.cs', '.csproj', '.json', '.md', '.props', '.ps1', '.slnx',
    '.txt', '.xaml', '.xml', '.yml', '.yaml'
)
$credentialPatterns = @(
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '\bgh[pousr]_[A-Za-z0-9_]{30,}\b',
    '\bgithub_pat_[A-Za-z0-9_]{30,}\b',
    '\bAKIA[0-9A-Z]{16}\b',
    '(?i)AccountKey\s*=\s*[A-Za-z0-9+/]{32,}={0,2}'
)
$privatePathPattern = '(?i)\bC:\\Users\\(?!Public(?:\\|\b)|Default(?:\\|\b))[^\\\s]+'
foreach ($file in $repositoryFiles |
    Where-Object { $_.Extension -in $textFileExtensions }) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $relativePath = Get-RepositoryRelativePath -Path $file.FullName
    foreach ($pattern in $credentialPatterns) {
        if ($content -match $pattern) {
            $failures.Add("Potential credential material found in $relativePath.")
            break
        }
    }
    if ($content -match $privatePathPattern) {
        $failures.Add("Machine-specific Windows user path found in $relativePath.")
    }
}

try {
    [xml]$nugetConfiguration = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot 'NuGet.Config') `
        -Raw
    $packageSources = @($nugetConfiguration.configuration.packageSources.add)
    $auditSources = @($nugetConfiguration.configuration.auditSources.add)
    if ($packageSources.Count -ne 1 -or
        $packageSources[0].key -cne 'nuget.org' -or
        $packageSources[0].value -cne 'https://api.nuget.org/v3/index.json' -or
        $auditSources.Count -ne 1 -or
        $auditSources[0].key -cne 'nuget.org' -or
        $auditSources[0].value -cne 'https://api.nuget.org/v3/index.json') {
        $failures.Add('NuGet.Config must use only the reviewed NuGet.org v3 source for restore and audit.')
    }
    $signatureMode = @(
        $nugetConfiguration.configuration.config.add |
            Where-Object { $_.key -ceq 'signatureValidationMode' }
    )
    if ($signatureMode.Count -ne 1 -or $signatureMode[0].value -cne 'require') {
        $failures.Add('NuGet.Config must require package signature validation.')
    }
    $trustedRepositories = @($nugetConfiguration.configuration.trustedSigners.repository)
    $expectedNuGetFingerprints = @(
        '0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D',
        '5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4',
        '1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D'
    ) | Sort-Object
    $actualNuGetFingerprints = @(
        $trustedRepositories.certificate |
            ForEach-Object { $_.fingerprint } |
            Sort-Object
    )
    $fingerprintDifference = @(
        Compare-Object `
            -ReferenceObject $expectedNuGetFingerprints `
            -DifferenceObject $actualNuGetFingerprints `
            -CaseSensitive
    )
    $invalidCertificatePolicy = @(
        $trustedRepositories.certificate |
            Where-Object {
                $_.hashAlgorithm -cne 'SHA256' -or
                $_.allowUntrustedRoot -cne 'false'
            }
    )
    if ($trustedRepositories.Count -ne 1 -or
        $trustedRepositories[0].name -cne 'nuget.org' -or
        $trustedRepositories[0].serviceIndex -cne 'https://api.nuget.org/v3/index.json' -or
        $fingerprintDifference.Count -ne 0 -or
        $actualNuGetFingerprints.Count -ne $expectedNuGetFingerprints.Count -or
        $invalidCertificatePolicy.Count -ne 0) {
        $failures.Add('NuGet.Config must trust exactly the reviewed NuGet.org repository signing certificates.')
    }
}
catch {
    $failures.Add("NuGet.Config is not valid XML: $($_.Exception.Message)")
}

$workflowPaths = @(
    Join-Path $repositoryRoot '.github\workflows\ci.yml'
    Join-Path $repositoryRoot '.github\workflows\codeql.yml'
    Join-Path $repositoryRoot '.github\workflows\release.yml'
)
foreach ($workflowPath in $workflowPaths |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) {
    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    if ($workflowText -notmatch '(?m)^permissions:\s*\r?\n\s+contents:\s+read\s*$') {
        $failures.Add("Workflow $([IO.Path]::GetFileName($workflowPath)) must declare top-level contents: read permissions.")
    }

    foreach ($match in [regex]::Matches($workflowText, '(?m)^\s*-?\s*uses:\s*([^\s#]+)')) {
        $actionReference = $match.Groups[1].Value
        if ($actionReference.StartsWith('./', [StringComparison]::Ordinal)) {
            continue
        }
        if ($actionReference -notmatch '@[0-9a-fA-F]{40}$') {
            $failures.Add("GitHub Action is not pinned to a full commit SHA: $actionReference")
        }
    }
}

$releaseWorkflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
if (Test-Path -LiteralPath $releaseWorkflowPath -PathType Leaf) {
    $releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
    $requiredReleaseControls = @(
        'environment:\s+release',
        'actions/attest@[0-9a-f]{40}',
        'artifact-metadata:\s+write',
        'attestations:\s+write',
        'id-token:\s+write',
        "Makmatoe/HandleScope",
        'RequireTagAtHead',
        'RequireMainAtHead',
        'Finalize-Release\.ps1',
        'Verify-ReleaseAssets\.ps1',
        'release-output'
    )
    foreach ($pattern in $requiredReleaseControls) {
        if ($releaseWorkflow -notmatch $pattern) {
            $failures.Add("Release workflow is missing required fail-closed control: $pattern")
        }
    }
    if ($releaseWorkflow -match '(?m)^\s*workflow_dispatch\s*:') {
        $failures.Add('Release workflow must not support manual publication dispatch.')
    }
    if ($releaseWorkflow -match '\$\{\{\s*secrets\.') {
        $failures.Add('Release workflow must not consume long-lived GitHub secrets.')
    }
    if ($releaseWorkflow -match '(?i)azure/artifact-signing|EXPECTED_PUBLISHER|APPROVED_LICENSE') {
        $failures.Add('Release workflow must not depend on paid or externally configured code signing.')
    }
    if ([regex]::Matches(
            $releaseWorkflow,
            '(?m)^\s+contents:\s+write\s*$').Count -ne 1) {
        $failures.Add('Only the final publication job may receive contents: write.')
    }
    if ([regex]::Matches(
            $releaseWorkflow,
            '(?m)^\s+environment:\s+release\s*$').Count -ne 1) {
        $failures.Add('Only the final publication job must use the protected release environment.')
    }
    if ($releaseWorkflow -match '(?m)\s--clobber(?:\s|$)') {
        $failures.Add('Release publication must not replace existing draft assets.')
    }
}

$installerSourcePath = Join-Path `
    $repositoryRoot `
    'HandleScope.Api\Scripts\Install-HandleScopeApi.ps1'
if (Test-Path -LiteralPath $installerSourcePath -PathType Leaf) {
    $installerSource = [IO.File]::ReadAllText($installerSourcePath)
    $verificationMarker = 'Release integrity check failed for $($sourceFile.Name).'
    $dotSourceCall = '. $commonScript'
    $verificationIndex = $installerSource.IndexOf(
        $verificationMarker,
        [StringComparison]::Ordinal)
    $dotSourceIndex = $installerSource.IndexOf(
        $dotSourceCall,
        [StringComparison]::Ordinal)
    if ($verificationIndex -lt 0 -or
        $dotSourceIndex -lt 0 -or
        $dotSourceIndex -le $verificationIndex -or
        $installerSource.IndexOf(
            'fixed nine-file allowlist',
            [StringComparison]::Ordinal) -lt 0) {
        $failures.Add('Release installer must enforce the fixed source allowlist and manifest hashes before dot-sourcing common code.')
    }
}

$sessionDockHelperPath = Join-Path `
    $repositoryRoot `
    'HandleScope.Api\Scripts\Enable-SessionDockIntegration.ps1'
if (Test-Path -LiteralPath $sessionDockHelperPath -PathType Leaf) {
    $sessionDockHelper = [IO.File]::ReadAllText($sessionDockHelperPath)
    $requiredHelperControls = @(
        'SupportsShouldProcess = $true',
        '[switch]$Force',
        "Get-HandleScopeLocalPath -RelativePath 'RobloxOne'",
        "Join-Path `$settingsDirectory 'handlescope.json'",
        '$PSCmdlet.ShouldProcess',
        '[IO.FileMode]::CreateNew',
        '$stream.Flush($true)',
        'Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force'
    )
    foreach ($control in $requiredHelperControls) {
        if ($sessionDockHelper.IndexOf(
                $control,
                [StringComparison]::Ordinal) -lt 0) {
            $failures.Add("SessionDock helper is missing its reviewed local-write control: $control")
        }
    }
    if ($sessionDockHelper -match '(?i)Start-Process|Invoke-WebRequest|Invoke-RestMethod|HttpClient|connection\.json|\btoken\b') {
        $failures.Add('SessionDock helper must not start software, use the network, or access HandleScope connection credentials.')
    }
}

$licensePath = Join-Path $repositoryRoot 'LICENSE.md'
if (Test-Path -LiteralPath $licensePath -PathType Leaf) {
    $approvedLicenseSha256 =
        'D160D2DF3EC45BBC238C19675D5D7C83086D4FB516B4A57647FBA85381465354'
    $licenseSha256 = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
    $licenseText = [IO.File]::ReadAllText($licensePath)
    if ($licenseSha256 -cne $approvedLicenseSha256 -or
        $licenseText -notmatch '(?m)^# MIT License\s*$' -or
        $licenseText -notmatch 'Permission is hereby granted, free of charge' -or
        $licenseText -notmatch 'THE SOFTWARE IS PROVIDED "AS IS"' -or
        $licenseText -match '(?i)all rights reserved') {
        $failures.Add('LICENSE.md must exactly match the reviewed MIT license text.')
    }
}

$powerShellFiles = $repositoryFiles |
    Where-Object { $_.Extension -in @('.ps1', '.psm1') }
foreach ($scriptFile in $powerShellFiles) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $scriptFile.FullName,
        [ref]$tokens,
        [ref]$parseErrors)
    foreach ($parseError in $parseErrors) {
        $relativePath = Get-RepositoryRelativePath -Path $scriptFile.FullName
        $failures.Add(
            "PowerShell parse error in ${relativePath}: $($parseError.Message)")
    }
}

& dotnet sln (Join-Path $repositoryRoot 'HandleScope.slnx') list | Out-Null
if ($LASTEXITCODE -ne 0) {
    $failures.Add('HandleScope.slnx could not be read by the pinned .NET SDK.')
}

Push-Location $repositoryRoot
try {
    & git diff --check -- .
    if ($LASTEXITCODE -ne 0) {
        $failures.Add('git diff --check reported whitespace errors.')
    }
}
finally {
    Pop-Location
}

if ($failures.Count -gt 0) {
    $message = "Repository verification failed:`n - " +
        ($failures -join "`n - ")
    throw $message
}

$successMessage = (
    'Repository verification passed: {0} projects, zero PackageReference entries, ' +
    'locked NuGet inputs, SHA-pinned workflows, and no local credentials, build artifacts, or signing keys.') -f
    $projectFiles.Count
Write-Host $successMessage
