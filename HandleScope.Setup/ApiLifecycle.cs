using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HandleScope.Services;

namespace HandleScope.Setup;

internal interface IApiLifecycle
{
    void Start();

    void Stop();
}

internal sealed class ApiLifecycle : IApiLifecycle
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly SetupPaths _paths;
    private readonly SetupIdentity _identity;
    private readonly ProcessIdentityService _identityService = new();

    internal ApiLifecycle(SetupPaths paths, SetupIdentity identity)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public void Start()
    {
        AssertInstalledExecutable();
        using var installedLease =
            BundleLease.AcquireInstalledApiLease(_paths.InstallRoot);
        var connection = TryReadConnection(out var connectionFailure);
        if (connection is not null)
        {
            var process = ValidateConnectionProcess(connection);
            if (IsHealthy(connection))
            {
                Console.WriteLine("HandleScope API is already running.");
                WriteConnectionSummary(connection);
                return;
            }
            throw new SetupSafetyException(
                $"The HandleScope API process {process.ProcessId} is running but its authenticated discovery state is not healthy. No new process was started.");
        }

        var installedState = InspectInstalledProcesses();
        if (installedState != InstalledProcessState.NotRunning)
        {
            throw new SetupSafetyException(
                "Setup could not prove that the installed API is stopped. No new process was started.");
        }
        if (connectionFailure is not null && File.Exists(_paths.ConnectionPath))
        {
            _paths.DeleteFileIfPresent(_paths.ConnectionPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.InstalledApiPath,
            WorkingDirectory = _paths.InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(
                    "HANDLESCOPE_SETUP_",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }
        using var launched = Process.Start(startInfo)
            ?? throw new SetupOperationException(
                "Windows could not start HandleScope.Api.exe.");
        var launchedIdentity = CaptureLaunchedProcess(launched);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        SetupSafetyException? readinessRefusal = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(250);
            var active = TryReadConnection(out _);
            if (active is null || active.ProcessId != launched.Id)
            {
                continue;
            }
            try
            {
                var activeIdentity = ValidateConnectionProcess(active);
                if (!IsSameAuthenticatedProcess(
                        launchedIdentity,
                        activeIdentity))
                {
                    throw new SetupSafetyException(
                        "Discovery does not identify the exact API child launched by setup.");
                }
                if (IsHealthy(active))
                {
                    Console.WriteLine(
                        "HandleScope API started in restricted standard-user mode.");
                    WriteConnectionSummary(active);
                    return;
                }
            }
            catch (SetupSafetyException exception)
            {
                readinessRefusal = exception;
                break;
            }
        }
        TerminateFailedLaunch(
            launched,
            launchedIdentity,
            _identityService.GetIdentity,
            TimeSpan.FromSeconds(10));
        if (File.Exists(_paths.ConnectionPath))
        {
            _paths.DeleteFileIfPresent(_paths.ConnectionPath);
        }
        if (readinessRefusal is not null)
        {
            throw new SetupSafetyException(
                "The launched API produced invalid authenticated discovery state and was stopped safely.",
                readinessRefusal);
        }
        throw new SetupOperationException(
            $"HandleScope API did not become ready and was stopped safely. See {_paths.LogPath}");
    }

    private HandleScope.Models.ProcessIdentity CaptureLaunchedProcess(
        Process launched)
    {
        try
        {
            // Retain an exact kernel handle before recording the child's identity.
            _ = launched.SafeHandle;
            var identity = _identityService.GetIdentity(launched.Id);
            AssertExpectedInstalledProcess(identity);
            return identity;
        }
        catch (Exception exception) when (exception is not SetupSafetyException)
        {
            try
            {
                if (launched.HasExited)
                {
                    throw new SetupOperationException(
                        "HandleScope.Api.exe exited before setup could authenticate it.",
                        exception);
                }
            }
            catch (InvalidOperationException)
            {
                throw new SetupOperationException(
                    "HandleScope.Api.exe exited before setup could authenticate it.",
                    exception);
            }
            throw new SetupSafetyException(
                "Setup could not authenticate the exact API child it launched and refused to terminate an unverified process.",
                exception);
        }
    }

    internal static void TerminateFailedLaunch(
        Process launched,
        HandleScope.Models.ProcessIdentity captured,
        Func<int, HandleScope.Models.ProcessIdentity> resolveIdentity,
        TimeSpan waitTimeout)
    {
        ArgumentNullException.ThrowIfNull(launched);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(resolveIdentity);
        if (waitTimeout < TimeSpan.Zero || launched.Id != captured.ProcessId)
        {
            throw new SetupSafetyException(
                "The failed-launch cleanup identity or timeout is invalid.");
        }

        try
        {
            _ = launched.SafeHandle;
            if (launched.HasExited)
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        HandleScope.Models.ProcessIdentity current;
        try
        {
            current = resolveIdentity(captured.ProcessId);
        }
        catch (Exception exception)
        {
            try
            {
                if (launched.HasExited)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new SetupSafetyException(
                "The failed API child could not be reauthenticated, so setup refused to terminate it.",
                exception);
        }
        if (!IsSameAuthenticatedProcess(captured, current))
        {
            throw new SetupSafetyException(
                "The failed API PID no longer has the captured identity. Setup refused to terminate it.");
        }

        try
        {
            if (launched.HasExited)
            {
                return;
            }
            launched.Kill(entireProcessTree: false);
            if (!launched.WaitForExit((int)waitTimeout.TotalMilliseconds))
            {
                throw new SetupOperationException(
                    "The exact failed API child did not terminate in time.");
            }
        }
        catch (InvalidOperationException)
        {
            // The exact retained process exited between reauthentication and kill.
        }
    }

    public void Stop()
    {
        var connection = TryReadConnection(out var connectionFailure);
        if (connection is null)
        {
            var installedState = InspectInstalledProcesses();
            if (installedState == InstalledProcessState.NotRunning)
            {
                if (connectionFailure is not null && File.Exists(_paths.ConnectionPath))
                {
                    _paths.DeleteFileIfPresent(_paths.ConnectionPath);
                    Console.WriteLine(
                        "Removed a definitively stale HandleScope connection file.");
                }
                else
                {
                    Console.WriteLine("HandleScope API is not running.");
                }
                return;
            }
            throw new SetupSafetyException(
                "The installed API may be running without an authentic connection document. Setup refused an unauthenticated termination.");
        }

        var processIdentity = ValidateConnectionProcess(connection);
        using var handler = CreateHandler();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(connection.BaseUrl + "/v1/shutdown", UriKind.Absolute));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            connection.Token);
        using var response = client.Send(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        EnsureBoundedResponse(response);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new SetupOperationException(
                $"HandleScope API rejected authenticated shutdown (HTTP {(int)response.StatusCode}).");
        }

        WaitForExactProcessExit(processIdentity);
        _paths.DeleteFileIfPresent(_paths.ConnectionPath);
        Console.WriteLine("HandleScope API stopped.");
    }

    private void WaitForExactProcessExit(
        HandleScope.Models.ProcessIdentity expected)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
        }
        catch (ArgumentException)
        {
            // The authenticated process exited between the response and wait.
            return;
        }

        using (process)
        {
            try
            {
                // Open and retain the kernel process handle before the second
                // identity check so WaitForExit cannot attach to a reused PID.
                _ = process.SafeHandle;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            HandleScope.Models.ProcessIdentity current;
            try
            {
                current = _identityService.GetIdentity(expected.ProcessId);
            }
            catch (Exception exception)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                throw new SetupSafetyException(
                    "The shutting-down HandleScope process could not be reauthenticated.",
                    exception);
            }

            if (!IsSameAuthenticatedProcess(expected, current))
            {
                // The original process is gone and this PID now identifies a
                // different process. Never wait for or terminate the replacement.
                return;
            }
            if (!process.WaitForExit(10_000))
            {
                throw new SetupOperationException(
                    "HandleScope API did not stop within 10 seconds.");
            }
        }
    }

    private static bool IsSameAuthenticatedProcess(
        HandleScope.Models.ProcessIdentity expected,
        HandleScope.Models.ProcessIdentity current) =>
        current.ProcessId == expected.ProcessId &&
        current.CreationTimeUtcFileTime == expected.CreationTimeUtcFileTime &&
        current.OwnerSid == expected.OwnerSid &&
        current.WindowsSessionId == expected.WindowsSessionId &&
        current.IsElevated == expected.IsElevated &&
        current.ProcessName == expected.ProcessName &&
        string.Equals(
            SetupPaths.Normalize(current.ImagePath),
            SetupPaths.Normalize(expected.ImagePath),
            StringComparison.OrdinalIgnoreCase);

    private ApiConnection? TryReadConnection(out Exception? failure)
    {
        failure = null;
        if (!File.Exists(_paths.ConnectionPath))
        {
            return null;
        }
        try
        {
            _paths.AssertContained(_paths.ConnectionPath);
            var file = new FileInfo(_paths.ConnectionPath);
            if (file.Length is <= 0 or > MaximumResponseBytes ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SetupSafetyException(
                    "The HandleScope connection document is not a small regular file.");
            }
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(file.FullName),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            var root = document.RootElement;
            var expected = new HashSet<string>(
                ["apiVersion", "baseUrl", "token", "processId", "startedAtUtc"],
                StringComparer.Ordinal);
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != expected.Count ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != expected.Count ||
                !properties.Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
                root.GetProperty("apiVersion").GetString() != "v1")
            {
                throw new SetupSafetyException(
                    "The HandleScope connection document has an invalid shape.");
            }
            var baseUrl = root.GetProperty("baseUrl").GetString();
            var token = root.GetProperty("token").GetString();
            var processId = root.GetProperty("processId").GetInt32();
            var startedText = root.GetProperty("startedAtUtc").GetString();
            if (baseUrl is null || !Uri.TryCreate(
                    baseUrl,
                    UriKind.Absolute,
                    out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                uri.Host != "127.0.0.1" ||
                uri.Port <= 0 ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                token is null || token.Length != 43 ||
                token.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not ('_' or '-')) ||
                processId <= 0 ||
                startedText is null ||
                !DateTimeOffset.TryParseExact(
                    startedText,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _))
            {
                throw new SetupSafetyException(
                    "The HandleScope connection document contains unsafe values.");
            }
            return new ApiConnection(
                uri.AbsoluteUri.TrimEnd('/'),
                token,
                processId);
        }
        catch (Exception exception) when (
            exception is SetupSafetyException or JsonException or IOException or
                UnauthorizedAccessException or InvalidOperationException or
                FormatException or OverflowException)
        {
            failure = exception;
            return null;
        }
    }

    private HandleScope.Models.ProcessIdentity ValidateConnectionProcess(
        ApiConnection connection)
    {
        HandleScope.Models.ProcessIdentity process;
        try
        {
            process = _identityService.GetIdentity(connection.ProcessId);
        }
        catch (Exception exception)
        {
            throw new SetupSafetyException(
                "The process identified by the connection document could not be inspected.",
                exception);
        }
        AssertExpectedInstalledProcess(process);
        return process;
    }

    private void AssertExpectedInstalledProcess(
        HandleScope.Models.ProcessIdentity process)
    {
        if (!string.Equals(
                SetupPaths.Normalize(process.ImagePath),
                SetupPaths.Normalize(_paths.InstalledApiPath),
                StringComparison.OrdinalIgnoreCase) ||
            process.OwnerSid != _identity.Sid ||
            process.WindowsSessionId != _identity.SessionId ||
            process.IsElevated ||
            process.ProcessName != "HandleScope.Api")
        {
            throw new SetupSafetyException(
                "The connection document does not identify this user's installed standard-privilege API.");
        }
    }

    private InstalledProcessState InspectInstalledProcesses()
    {
        var incomplete = false;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("HandleScope.Api");
        }
        catch
        {
            return InstalledProcessState.Unknown;
        }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var identity = _identityService.GetIdentity(process.Id);
                    if (string.Equals(
                            SetupPaths.Normalize(identity.ImagePath),
                            SetupPaths.Normalize(_paths.InstalledApiPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return InstalledProcessState.Running;
                    }
                }
                catch
                {
                    incomplete = true;
                }
            }
        }
        return incomplete
            ? InstalledProcessState.Unknown
            : InstalledProcessState.NotRunning;
    }

    private bool IsHealthy(ApiConnection connection)
    {
        try
        {
            using var handler = CreateHandler();
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(connection.BaseUrl + "/v1/health", UriKind.Absolute));
            using var response = client.Send(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            var bytes = ReadBoundedResponse(response);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var expected = new HashSet<string>(
                ["status", "apiVersion", "policy"],
                StringComparer.Ordinal);
            var properties = root.EnumerateObject().ToArray();
            return properties.Length == expected.Count &&
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() == expected.Count &&
                properties.Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expected) &&
                root.GetProperty("status").GetString() == "ready" &&
                root.GetProperty("apiVersion").GetString() == "v1" &&
                root.GetProperty("policy").GetString() ==
                    "roblox-singleton-event-v1";
        }
        catch
        {
            return false;
        }
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        Credentials = null,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 16,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null
    };

    private static void EnsureBoundedResponse(HttpResponseMessage response)
    {
        _ = ReadBoundedResponse(response);
    }

    private static byte[] ReadBoundedResponse(HttpResponseMessage response)
    {
        if (response.Headers.Location is not null ||
            response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new SetupOperationException(
                "The local API returned an unsafe response.");
        }
        using var input = response.Content.ReadAsStream();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = input.Read(buffer);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumResponseBytes)
            {
                throw new SetupOperationException(
                    "The local API response exceeded its size limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private void AssertInstalledExecutable()
    {
        if (!File.Exists(_paths.InstalledApiPath))
        {
            throw new SetupOperationException(
                "HandleScope.Api.exe is not installed for this user.");
        }
        _paths.AssertTreeSafe(_paths.InstallRoot);
    }

    private void WriteConnectionSummary(ApiConnection connection)
    {
        Console.WriteLine($"API URL: {connection.BaseUrl}");
        Console.WriteLine($"API process ID: {connection.ProcessId}");
        Console.WriteLine($"Connection file: {_paths.ConnectionPath}");
    }

    private sealed record ApiConnection(
        string BaseUrl,
        string Token,
        int ProcessId);

    private enum InstalledProcessState
    {
        NotRunning,
        Running,
        Unknown
    }
}
