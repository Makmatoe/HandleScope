[CmdletBinding()]
param(
    [switch]$EnableAutostart,
    [switch]$EnableSessionDock,
    [switch]$StartNow,
    [switch]$AllowDowngrade,
    [switch]$VerifyOnly
)

Microsoft.PowerShell.Core\Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'HandleScope.Setup.exe'))
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw 'HandleScope.Setup.exe was not found next to the compatibility wrapper.'
}

$nativeArguments = [Collections.Generic.List[string]]::new()
if ($VerifyOnly) {
    if ($EnableAutostart -or $EnableSessionDock -or $StartNow -or
        $AllowDowngrade) {
        throw '-VerifyOnly cannot be combined with installation options.'
    }
    $nativeArguments.Add('verify')
}
else {
    $nativeArguments.Add('install')
    if ($StartNow) { $nativeArguments.Add('--start-now') }
    if ($EnableAutostart) { $nativeArguments.Add('--enable-autostart') }
    if ($EnableSessionDock) { $nativeArguments.Add('--enable-sessiondock') }
    if ($AllowDowngrade) { $nativeArguments.Add('--allow-downgrade') }
}

& $setup @nativeArguments
if ($LASTEXITCODE -ne 0) {
    throw "HandleScope.Setup.exe failed with exit code $LASTEXITCODE."
}
