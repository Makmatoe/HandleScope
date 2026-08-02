[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$commonScript = Join-Path `
    $repositoryRoot `
    'HandleScope.Api\Scripts\HandleScope.ScriptCommon.ps1'
. $commonScript

function Assert-CompatibilityEqual {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Expected,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Actual
    )

    if ($Actual -cne $Expected) {
        throw "$Scenario returned '$Actual'; expected '$Expected'."
    }
}

$startupScenarios = @(
    [pscustomobject]@{
        Name = 'ready connection'
        Connection = 'Valid'
        Health = $true
        Referenced = 'None'
        Installed = 'Unknown'
        Expected = 'Ready'
    },
    [pscustomobject]@{
        Name = 'unhealthy valid connection with no live process'
        Connection = 'Valid'
        Health = $false
        Referenced = 'None'
        Installed = 'NotRunning'
        Expected = 'Start'
    },
    [pscustomobject]@{
        Name = 'unhealthy valid connection with referenced process'
        Connection = 'Valid'
        Health = $false
        Referenced = 'Running'
        Installed = 'Running'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'unhealthy valid connection with uncertain process inspection'
        Connection = 'Valid'
        Health = $false
        Referenced = 'Unknown'
        Installed = 'Unknown'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'referenced process still running'
        Connection = 'Invalid'
        Health = $false
        Referenced = 'Running'
        Installed = 'NotRunning'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'referenced process inspection uncertain'
        Connection = 'Invalid'
        Health = $false
        Referenced = 'Unknown'
        Installed = 'NotRunning'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'installed process still running'
        Connection = 'Invalid'
        Health = $false
        Referenced = 'None'
        Installed = 'Running'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'installed process inspection uncertain'
        Connection = 'Missing'
        Health = $false
        Referenced = 'None'
        Installed = 'Unknown'
        Expected = 'Blocked'
    },
    [pscustomobject]@{
        Name = 'definitely stale connection'
        Connection = 'Invalid'
        Health = $false
        Referenced = 'None'
        Installed = 'NotRunning'
        Expected = 'Start'
    },
    [pscustomobject]@{
        Name = 'clean startup'
        Connection = 'Missing'
        Health = $false
        Referenced = 'None'
        Installed = 'NotRunning'
        Expected = 'Start'
    }
)
foreach ($scenario in $startupScenarios) {
    $actualDisposition = Resolve-HandleScopeApiStartupDisposition `
        -ConnectionState $scenario.Connection `
        -HealthReady $scenario.Health `
        -ReferencedProcessState $scenario.Referenced `
        -InstalledProcessState $scenario.Installed
    Assert-CompatibilityEqual `
        -Scenario $scenario.Name `
        -Expected $scenario.Expected `
        -Actual $actualDisposition
}

$expectedApiExecutable = Join-Path `
    ([IO.Path]::GetTempPath()) `
    'HandleScope\HandleScope.Api.exe'
function New-CompatibilityAutostartTask {
    param(
        [string]$Execute = $expectedApiExecutable,
        [string]$Arguments = '',
        [string]$RunLevel = 'Limited',
        [string]$State = 'Ready',
        [bool]$Enabled = $true
    )

    [pscustomobject]@{
        Actions = @(
            [pscustomobject]@{
                Execute = $Execute
                Arguments = $Arguments
            }
        )
        Principal = [pscustomobject]@{
            RunLevel = $RunLevel
        }
        State = $State
        Settings = [pscustomobject]@{
            Enabled = $Enabled
        }
    }
}

$autostartScenarios = @(
    [pscustomobject]@{
        Name = 'missing autostart task'
        Task = $null
        Expected = 'Absent'
    },
    [pscustomobject]@{
        Name = 'enabled autostart task'
        Task = New-CompatibilityAutostartTask `
            -Execute $expectedApiExecutable.ToUpperInvariant()
        Expected = 'Enabled'
    },
    [pscustomobject]@{
        Name = 'disabled autostart task state'
        Task = New-CompatibilityAutostartTask -State 'Disabled'
        Expected = 'Disabled'
    },
    [pscustomobject]@{
        Name = 'disabled autostart task setting'
        Task = New-CompatibilityAutostartTask -Enabled $false
        Expected = 'Disabled'
    },
    [pscustomobject]@{
        Name = 'unexpected autostart executable'
        Task = New-CompatibilityAutostartTask `
            -Execute (Join-Path ([IO.Path]::GetTempPath()) 'Other.exe')
        Expected = 'Unexpected'
    },
    [pscustomobject]@{
        Name = 'unexpected autostart arguments'
        Task = New-CompatibilityAutostartTask -Arguments '--unexpected'
        Expected = 'Unexpected'
    },
    [pscustomobject]@{
        Name = 'elevated autostart task'
        Task = New-CompatibilityAutostartTask -RunLevel 'Highest'
        Expected = 'Unexpected'
    }
)
foreach ($scenario in $autostartScenarios) {
    $actualAutostartState = Get-HandleScopeAutostartState `
        -Task $scenario.Task `
        -ExpectedExecutable $expectedApiExecutable
    Assert-CompatibilityEqual `
        -Scenario $scenario.Name `
        -Expected $scenario.Expected `
        -Actual $actualAutostartState
}

function Assert-NativeCompatibilityWrapper {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedParameters,

        [Parameter(Mandatory)]
        [string[]]$RequiredMarkers
    )

    $path = Join-Path $repositoryRoot $RelativePath
    $source = [IO.File]::ReadAllText($path)
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "Compatibility wrapper has a parse error: $RelativePath"
    }

    $actualParameters = @(
        $ast.ParamBlock.Parameters |
            ForEach-Object { $_.Name.VariablePath.UserPath }
    )
    $parameterDifference = @(
        Compare-Object `
            -ReferenceObject $ExpectedParameters `
            -DifferenceObject $actualParameters `
            -CaseSensitive
    )
    if ($parameterDifference.Count -ne 0 -or
        $ExpectedParameters.Count -ne $actualParameters.Count) {
        throw "Compatibility wrapper parameter contract changed: $RelativePath"
    }

    foreach ($requiredMarker in @(
            "'HandleScope.Setup.exe'",
            'HandleScope.Setup.exe was not found next to the compatibility wrapper.',
            '$LASTEXITCODE -ne 0',
            'HandleScope.Setup.exe failed with exit code') + $RequiredMarkers) {
        if ($source.IndexOf(
                $requiredMarker,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Compatibility wrapper is missing '$requiredMarker': $RelativePath"
        }
    }

    $invocations = @(
        $ast.FindAll(
            {
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.InvocationOperator -eq
                    [Management.Automation.Language.TokenKind]::Ampersand
            },
            $true)
    )
    if ($invocations.Count -ne 1 -or
        $invocations[0].Extent.Text -cnotmatch '^&\s+\$setup(?:\s|$)') {
        throw "Compatibility wrapper must invoke only the adjacent native setup executable: $RelativePath"
    }

    $dotInvocations = @(
        $ast.FindAll(
            {
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.InvocationOperator -eq
                    [Management.Automation.Language.TokenKind]::Dot
            },
            $true)
    )
    $forbiddenCommandNames = @(
        'cmd',
        'Copy-Item',
        'Invoke-Expression',
        'Invoke-RestMethod',
        'Invoke-WebRequest',
        'Move-Item',
        'powershell',
        'pwsh',
        'Register-ScheduledTask',
        'Remove-Item',
        'Set-ExecutionPolicy',
        'Start-Process',
        'Stop-Process',
        'Unregister-ScheduledTask'
    )
    $forbiddenCommands = @(
        $ast.FindAll(
            {
                param($node)
                if ($node -isnot [Management.Automation.Language.CommandAst]) {
                    return $false
                }
                $commandName = $node.GetCommandName()
                return -not [string]::IsNullOrWhiteSpace($commandName) -and
                    ($commandName -in $forbiddenCommandNames -or
                     $commandName -match '(?i)\.(?:ps1|psm1|bat|cmd|com)$')
            },
            $true)
    )
    if ($dotInvocations.Count -ne 0 -or
        $forbiddenCommands.Count -ne 0 -or
        $source.IndexOf(
            'HandleScope.ScriptCommon.ps1',
            [StringComparison]::Ordinal) -ge 0 -or
        $source -match '(?i)-ExecutionPolicy|\bBypass\b|\bUnrestricted\b') {
        throw "Compatibility wrapper gained lifecycle, shell, policy, or network logic: $RelativePath"
    }
}

$wrapperCases = @(
    [pscustomobject]@{
        RelativePath = 'HandleScope.Api\Scripts\Install-HandleScopeApi.ps1'
        ExpectedParameters = @(
            'EnableAutostart',
            'EnableSessionDock',
            'StartNow',
            'AllowDowngrade',
            'VerifyOnly'
        )
        RequiredMarkers = @(
            '-VerifyOnly cannot be combined with installation options.',
            '$nativeArguments.Add(''verify'')',
            '$nativeArguments.Add(''install'')',
            '$nativeArguments.Add(''--start-now'')',
            '$nativeArguments.Add(''--enable-autostart'')',
            '$nativeArguments.Add(''--enable-sessiondock'')',
            '$nativeArguments.Add(''--allow-downgrade'')'
        )
    },
    [pscustomobject]@{
        RelativePath = 'HandleScope.Api\Scripts\Start-HandleScopeApi.ps1'
        ExpectedParameters = @()
        RequiredMarkers = @('''start''')
    },
    [pscustomobject]@{
        RelativePath = 'HandleScope.Api\Scripts\Stop-HandleScopeApi.ps1'
        ExpectedParameters = @()
        RequiredMarkers = @('''stop''')
    },
    [pscustomobject]@{
        RelativePath = 'HandleScope.Api\Scripts\Uninstall-HandleScopeApi.ps1'
        ExpectedParameters = @('KeepDiagnostics')
        RequiredMarkers = @(
            '$nativeArguments.Add(''uninstall'')',
            '$nativeArguments.Add(''--keep-diagnostics'')'
        )
    },
    [pscustomobject]@{
        RelativePath =
            'HandleScope.Api\Scripts\Enable-SessionDockIntegration.ps1'
        ExpectedParameters = @('Force')
        RequiredMarkers = @(
            'SupportsShouldProcess = $true',
            '$PSCmdlet.ShouldProcess',
            '$nativeArguments.Add(''enable-sessiondock'')',
            '$nativeArguments.Add(''--force'')'
        )
    }
)
foreach ($wrapperCase in $wrapperCases) {
    Assert-NativeCompatibilityWrapper `
        -RelativePath $wrapperCase.RelativePath `
        -ExpectedParameters $wrapperCase.ExpectedParameters `
        -RequiredMarkers $wrapperCase.RequiredMarkers
}

Write-Host 'Windows PowerShell common-code and native-wrapper compatibility validation passed.'
