[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Tag,

    [switch]$RequireTagAtHead,

    [switch]$RequireMainAtHead,

    [switch]$RequireCleanWorkingTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $result = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($result)
}

Push-Location $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'Verify-Repository.ps1')

    [xml]$buildProperties = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') `
        -Raw
    $versions = @(
        $buildProperties.SelectNodes('/Project/PropertyGroup/Version') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($versions.Count -ne 1 -or $versions[0] -cnotmatch '^\d+\.\d+\.\d+$') {
        throw 'Directory.Build.props must contain exactly one stable MAJOR.MINOR.PATCH Version.'
    }

    $version = [string]$versions[0]
    if ($Tag -cne "v$version") {
        throw "Tag '$Tag' must exactly match project version '$version' as v$version."
    }

    $notesPath = Join-Path $repositoryRoot "ReleaseNotes\$version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Release notes are required at ReleaseNotes/$version.md."
    }

    $notes = Get-Content -LiteralPath $notesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 65536) {
        throw 'Release notes must contain between 1 and 65,536 characters.'
    }
    if ($notes -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') {
        throw 'Release notes contain unsupported control characters.'
    }

    $changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'
    $changelog = Get-Content -LiteralPath $changelogPath -Raw
    if ($changelog -notmatch "(?m)^## $([regex]::Escape($version))(?:\s+-\s+\d{4}-\d{2}-\d{2})?\s*$") {
        throw "CHANGELOG.md does not contain a release heading for $version."
    }

    $head = ((Invoke-Git -Arguments @('rev-parse', 'HEAD')) -join '').Trim()
    if ($head -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the current Git commit.'
    }

    if ($RequireCleanWorkingTree) {
        $workingTreeChanges = @(
            Invoke-Git -Arguments @(
                'status', '--porcelain=v1', '--untracked-files=normal'))
        if ($workingTreeChanges.Count -ne 0) {
            throw 'Release packaging requires a clean working tree.'
        }
    }

    if ($RequireTagAtHead) {
        $tagType = ((Invoke-Git -Arguments @('cat-file', '-t', "refs/tags/$Tag")) -join '').Trim()
        if ($tagType -cne 'tag') {
            throw "Release tag '$Tag' must be an annotated tag, not a movable lightweight tag."
        }

        $tagCommit = ((Invoke-Git -Arguments @('rev-list', '-n', '1', "refs/tags/$Tag")) -join '').Trim()
        if ($tagCommit -cne $head) {
            throw "Tag '$Tag' does not resolve to the checked-out commit."
        }
    }

    if ($RequireMainAtHead) {
        $mainCommit = ((Invoke-Git -Arguments @('rev-parse', 'refs/remotes/origin/main')) -join '').Trim()
        if ($mainCommit -cne $head) {
            throw "Release tags must point at the current origin/main commit. HEAD=$head origin/main=$mainCommit"
        }
    }

    Write-Host "Release metadata is aligned for $Tag at $head."
}
finally {
    Pop-Location
}
