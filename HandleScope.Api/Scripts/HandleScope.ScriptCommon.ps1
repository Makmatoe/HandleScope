Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

function Test-HandleScopeAdministratorToken {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-HandleScopeFileSystemItemNotLink {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [IO.FileSystemInfo]$Item
    )

    $linkType = $Item.PSObject.Properties['LinkType']
    if (($null -ne $linkType -and
         -not [string]::IsNullOrWhiteSpace([string]$linkType.Value)) -or
        (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing to use a HandleScope path that contains a file-system link or reparse point: $($Item.FullName)"
    }
}

function Get-HandleScopeLocalApplicationDataRoot {
    [CmdletBinding()]
    param()

    $root = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($root) -or
        -not [IO.Path]::IsPathRooted($root)) {
        throw 'Windows did not provide a valid Local Application Data directory.'
    }

    try {
        $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd(
            [char[]]@(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar))
        $rootItem = Get-Item `
            -LiteralPath $fullRoot `
            -Force `
            -ErrorAction Stop
    }
    catch {
        throw 'Windows provided an invalid Local Application Data directory.'
    }

    if (-not $rootItem.PSIsContainer) {
        throw 'Windows Local Application Data path is not a directory.'
    }
    Assert-HandleScopeFileSystemItemNotLink -Item $rootItem
    return $fullRoot
}

function Assert-HandleScopeLocalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not [IO.Path]::IsPathRooted($Path)) {
        throw 'HandleScope local paths must be non-empty absolute paths.'
    }

    $localRoot = Get-HandleScopeLocalApplicationDataRoot
    try {
        $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd(
            [char[]]@(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar))
    }
    catch {
        throw 'HandleScope encountered an invalid local path.'
    }

    $localPrefix = $localRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $localPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a path outside the current user's Local Application Data directory: $fullPath"
    }

    $relativePath = $fullPath.Substring($localPrefix.Length)
    $segments = $relativePath.Split(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)
    $currentPath = $localRoot
    foreach ($segment in $segments) {
        $currentPath = Join-Path $currentPath $segment
        try {
            $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        }
        catch {
            if ($_.CategoryInfo.Category -eq
                [Management.Automation.ErrorCategory]::ObjectNotFound) {
                break
            }

            throw "HandleScope could not safely validate a local path component: $currentPath"
        }

        Assert-HandleScopeFileSystemItemNotLink -Item $item
    }

    return $fullPath
}

function Get-HandleScopeLocalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RelativePath
    )

    if ([IO.Path]::IsPathRooted($RelativePath)) {
        throw 'HandleScope local relative paths cannot be rooted.'
    }

    $root = Get-HandleScopeLocalApplicationDataRoot
    return Assert-HandleScopeLocalPath -Path (Join-Path $root $RelativePath)
}

function Get-HandleScopeLocalProgramsRoot {
    [CmdletBinding()]
    param()

    return Get-HandleScopeLocalPath -RelativePath 'Programs'
}

function Get-HandleScopeInstallRoot {
    [CmdletBinding()]
    param()

    return Get-HandleScopeLocalPath -RelativePath 'Programs\HandleScope\Api'
}

function Get-HandleScopeRuntimeRoot {
    [CmdletBinding()]
    param()

    return Get-HandleScopeLocalPath -RelativePath 'HandleScope'
}

function Get-HandleScopeConnectionPath {
    [CmdletBinding()]
    param()

    return Get-HandleScopeLocalPath -RelativePath 'HandleScope\connection.json'
}

function Get-HandleScopeLogPath {
    [CmdletBinding()]
    param()

    return Get-HandleScopeLocalPath -RelativePath 'HandleScope\api.log'
}

function Assert-HandleScopeLocalTreeSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $safePath = Assert-HandleScopeLocalPath -Path $Path
    try {
        $rootItem = Get-Item `
            -LiteralPath $safePath `
            -Force `
            -ErrorAction Stop
    }
    catch {
        if ($_.CategoryInfo.Category -eq
            [Management.Automation.ErrorCategory]::ObjectNotFound) {
            return $safePath
        }

        throw "HandleScope could not safely inspect a local path: $safePath"
    }

    Assert-HandleScopeFileSystemItemNotLink -Item $rootItem
    if (-not $rootItem.PSIsContainer) {
        return $safePath
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($safePath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $children = @(Get-ChildItem `
            -LiteralPath $directory `
            -Force `
            -ErrorAction Stop)
        foreach ($child in $children) {
            Assert-HandleScopeFileSystemItemNotLink -Item $child
            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
        }
    }

    return $safePath
}

function Remove-HandleScopeLocalItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$Recurse,

        [switch]$IgnoreMissing
    )

    $safePath = if ($Recurse) {
        Assert-HandleScopeLocalTreeSafe -Path $Path
    }
    else {
        Assert-HandleScopeLocalPath -Path $Path
    }

    try {
        $item = Get-Item `
            -LiteralPath $safePath `
            -Force `
            -ErrorAction Stop
    }
    catch {
        if ($IgnoreMissing -and
            $_.CategoryInfo.Category -eq
                [Management.Automation.ErrorCategory]::ObjectNotFound) {
            return
        }

        throw "HandleScope could not safely remove a local path: $safePath"
    }

    Assert-HandleScopeFileSystemItemNotLink -Item $item
    if ($Recurse -and -not $item.PSIsContainer) {
        throw "Refusing a recursive removal because the HandleScope path is not a directory: $safePath"
    }

    Remove-Item `
        -LiteralPath $safePath `
        -Force `
        -Recurse:$Recurse `
        -ErrorAction Stop
}

function Get-HandleScopeTaskIdentity {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    [pscustomobject]@{
        Name = $identity.Name
        Sid = $sid
        TaskName = 'Local API'
        TaskPath = "\HandleScope\$sid\"
    }
}

function Get-HandleScopeConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $safePath = Assert-HandleScopeLocalPath -Path $Path
    if (-not (Test-Path -LiteralPath $safePath -PathType Leaf)) {
        throw "HandleScope connection file was not found: $safePath"
    }

    $connectionFile = Get-Item -LiteralPath $safePath -Force
    Assert-HandleScopeFileSystemItemNotLink -Item $connectionFile

    try {
        $connection = Get-Content -LiteralPath $safePath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'HandleScope connection file is invalid JSON.'
    }

    $baseUri = $null
    $baseUrl = [string]$connection.baseUrl
    if (-not [Uri]::TryCreate(
            $baseUrl,
            [UriKind]::Absolute,
            [ref]$baseUri) -or
        $baseUri.Scheme -cne [Uri]::UriSchemeHttp -or
        $baseUri.Host -cne '127.0.0.1' -or
        $baseUri.Port -le 0 -or
        $baseUri.AbsolutePath -cne '/' -or
        -not [string]::IsNullOrEmpty($baseUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($baseUri.Query) -or
        -not [string]::IsNullOrEmpty($baseUri.Fragment)) {
        throw 'HandleScope connection file contains an unsafe API URL.'
    }

    $token = [string]$connection.token
    if ($token -cnotmatch '^[A-Za-z0-9_-]{43}$') {
        throw 'HandleScope connection file contains an invalid API token.'
    }

    if ([string]$connection.apiVersion -cne 'v1') {
        throw 'HandleScope connection file uses an unsupported API version.'
    }

    $processId = 0
    if (-not [int]::TryParse(
            [string]$connection.processId,
            [ref]$processId) -or
        $processId -le 0) {
        throw 'HandleScope connection file contains an invalid process ID.'
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.ProcessName -cne 'HandleScope.Api') {
        throw 'HandleScope connection file does not identify the running API process.'
    }

    [pscustomobject]@{
        apiVersion = 'v1'
        baseUrl = $baseUri.AbsoluteUri.TrimEnd('/')
        token = $token
        processId = $processId
        startedAtUtc = $connection.startedAtUtc
    }
}

function Invoke-HandleScopeApiRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Connection,

        [Parameter(Mandatory)]
        [ValidatePattern('^/v1/[a-z/]+$')]
        [string]$Path,

        [ValidateSet('GET', 'POST')]
        [string]$Method = 'GET',

        [string]$Body,

        [switch]$Authenticated,

        [ValidateRange(1, 15)]
        [int]$TimeoutSeconds = 5,

        [ValidateRange(1024, 1048576)]
        [int]$MaximumResponseBytes = 1048576
    )

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $request = $null
    $response = $null
    $stream = $null
    $buffer = $null
    $cancellation = [Threading.CancellationTokenSource]::new(
        [TimeSpan]::FromSeconds($TimeoutSeconds))

    try {
        $uri = [Uri]::new($Connection.baseUrl.TrimEnd('/') + $Path)
        $request = [Net.Http.HttpRequestMessage]::new(
            [Net.Http.HttpMethod]::new($Method),
            $uri)
        if ($Authenticated) {
            $request.Headers.Authorization =
                [Net.Http.Headers.AuthenticationHeaderValue]::new(
                    'Bearer',
                    $Connection.token)
        }
        if ($PSBoundParameters.ContainsKey('Body')) {
            $request.Content = [Net.Http.StringContent]::new(
                $Body,
                [Text.Encoding]::UTF8,
                'application/json')
        }

        $sendTask = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $cancellation.Token)
        $response = $sendTask.GetAwaiter().GetResult()
        if ($response.Headers.Location) {
            throw 'HandleScope API returned a redirect, which is not allowed.'
        }
        if ($response.Content.Headers.ContentLength -and
            $response.Content.Headers.ContentLength.Value -gt $MaximumResponseBytes) {
            throw 'HandleScope API response exceeded the size limit.'
        }

        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = [IO.MemoryStream]::new()
        $chunk = New-Object byte[] 8192
        while ($true) {
            $readTask = $stream.ReadAsync(
                $chunk,
                0,
                $chunk.Length,
                $cancellation.Token)
            $read = $readTask.GetAwaiter().GetResult()
            if ($read -eq 0) {
                break
            }
            if ($buffer.Length + $read -gt $MaximumResponseBytes) {
                throw 'HandleScope API response exceeded the size limit.'
            }
            $buffer.Write($chunk, 0, $read)
        }

        $text = [Text.Encoding]::UTF8.GetString($buffer.ToArray())
        $json = $null
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            try {
                $json = $text | ConvertFrom-Json
            }
            catch {
                throw 'HandleScope API returned invalid JSON.'
            }
        }

        [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            IsSuccess = $response.IsSuccessStatusCode
            Json = $json
        }
    }
    finally {
        if ($buffer) { $buffer.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $cancellation.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

function Test-HandleScopeApiConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Connection,

        [int]$TimeoutSeconds = 2
    )

    try {
        $response = Invoke-HandleScopeApiRequest `
            -Connection $Connection `
            -Path '/v1/health' `
            -TimeoutSeconds $TimeoutSeconds `
            -MaximumResponseBytes 65536
        return $response.StatusCode -eq 200 -and
            $response.Json.status -ceq 'ready' -and
            $response.Json.apiVersion -ceq 'v1' -and
            $response.Json.policy -ceq 'roblox-singleton-event-v1'
    }
    catch {
        return $false
    }
}

function Write-HandleScopeConnectionSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Connection,

        [Parameter(Mandatory)]
        [string]$Path
    )

    Write-Host "API URL: $($Connection.baseUrl)"
    Write-Host "API process ID: $($Connection.processId)"
    Write-Host "Connection file: $Path"
}
