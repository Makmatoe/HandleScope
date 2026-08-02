[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [switch]$Force
)

Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$setup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'HandleScope.Setup.exe'))
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw 'HandleScope.Setup.exe was not found next to the compatibility wrapper.'
}
if (-not $PSCmdlet.ShouldProcess(
        "$env:LOCALAPPDATA\SessionDock\handlescope.json",
        'Enable the optional HandleScope integration')) {
    return
}
$nativeArguments = [Collections.Generic.List[string]]::new()
$nativeArguments.Add('enable-sessiondock')
if ($Force) { $nativeArguments.Add('--force') }
& $setup @nativeArguments
if ($LASTEXITCODE -ne 0) {
    throw "HandleScope.Setup.exe failed with exit code $LASTEXITCODE."
}
