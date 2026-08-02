[CmdletBinding()]
param(
    [switch]$KeepDiagnostics
)

Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$setup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'HandleScope.Setup.exe'))
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw 'HandleScope.Setup.exe was not found next to the compatibility wrapper.'
}
$nativeArguments = [Collections.Generic.List[string]]::new()
$nativeArguments.Add('uninstall')
if ($KeepDiagnostics) { $nativeArguments.Add('--keep-diagnostics') }
& $setup @nativeArguments
if ($LASTEXITCODE -ne 0) {
    throw "HandleScope.Setup.exe failed with exit code $LASTEXITCODE."
}
