[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$CI,

    [switch]$SkipControlledIntegration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'HandleScope.slnx'
$integrationProject = Join-Path `
    $repositoryRoot `
    'HandleScope.IntegrationTests\HandleScope.IntegrationTests.csproj'
$setupTestsProject = Join-Path `
    $repositoryRoot `
    'HandleScope.Setup.Tests\HandleScope.Setup.Tests.csproj'
$powerShellCompatibilityTest = Join-Path `
    $repositoryRoot `
    'scripts\Test-PowerShellCompatibility.ps1'
$windowsPowerShell = Join-Path `
    $env:SystemRoot `
    'System32\WindowsPowerShell\v1.0\powershell.exe'

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

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'HandleScope can be built and tested only on Windows.'
}

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1')

    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
        throw 'Windows PowerShell is required for the compatibility regression test.'
    }
    & $windowsPowerShell `
        -NoProfile `
        -NonInteractive `
        -File $powerShellCompatibilityTest
    if ($LASTEXITCODE -ne 0) {
        throw "Windows PowerShell compatibility validation failed with exit code $LASTEXITCODE."
    }

    Invoke-DotNet -Arguments @(
        'restore'
        $solutionPath
        '--locked-mode'
        '--nologo'
    )

    $continuousIntegration = if ($CI) { 'true' } else { 'false' }
    Invoke-DotNet -Arguments @(
        'build'
        $solutionPath
        '--configuration'
        $Configuration
        '--no-restore'
        '--nologo'
        "-p:ContinuousIntegrationBuild=$continuousIntegration"
    )

    if (-not $SkipControlledIntegration) {
        Invoke-DotNet -Arguments @(
            'run'
            '--project'
            $integrationProject
            '--configuration'
            $Configuration
            '--no-build'
            '--no-restore'
        )
        Invoke-DotNet -Arguments @(
            'run'
            '--project'
            $setupTestsProject
            '--configuration'
            $Configuration
            '--no-build'
            '--no-restore'
        )
    }

    Write-Host 'HandleScope validation completed successfully.'
}
finally {
    Pop-Location
}
