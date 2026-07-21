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
    }

    Write-Host 'HandleScope validation completed successfully.'
}
finally {
    Pop-Location
}
