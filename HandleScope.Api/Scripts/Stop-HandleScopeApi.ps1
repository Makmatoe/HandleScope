[CmdletBinding()]
param()

Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$setup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'HandleScope.Setup.exe'))
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw 'HandleScope.Setup.exe was not found next to the compatibility wrapper.'
}
& $setup 'stop'
if ($LASTEXITCODE -ne 0) {
    throw "HandleScope.Setup.exe failed with exit code $LASTEXITCODE."
}
