using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HandleScope.Api;
using HandleScope.Models;
using HandleScope.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--target", var targetPath, var targetEventName])
{
    using var heldFile = new FileStream(
        targetPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);
    using var heldEvent = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        targetEventName);
    Console.WriteLine("READY");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var temporaryPath = Path.Combine(
    Path.GetTempPath(),
    $"HandleScope-integration-{Guid.NewGuid():N}.lock");
var eventName = $"HandleScope-integration-{Guid.NewGuid():N}";
await File.WriteAllTextAsync(temporaryPath, "controlled integration test");

var connectionTestPath = Path.Combine(
    Path.GetTempPath(),
    $"HandleScope-connection-{Guid.NewGuid():N}.json");
ConnectionFile.Write(
    connectionTestPath,
    new ApiConnection(
        "v1",
        "http://127.0.0.1:47831",
        "integration-token",
        Environment.ProcessId,
        DateTimeOffset.UtcNow));

try
{
    using var connectionDocument = JsonDocument.Parse(
        await File.ReadAllTextAsync(connectionTestPath));
    if (!connectionDocument.RootElement.TryGetProperty("baseUrl", out _) ||
        !connectionDocument.RootElement.TryGetProperty("processId", out _) ||
        connectionDocument.RootElement.TryGetProperty("BaseUrl", out _) ||
        connectionDocument.RootElement.TryGetProperty("ProcessId", out _))
    {
        throw new InvalidOperationException(
            "The HandleScope connection document is not consistently camelCase.");
    }
}
finally
{
    ConnectionFile.DeleteIfOwned(connectionTestPath, Environment.ProcessId);
}

if (File.Exists(connectionTestPath))
{
    throw new InvalidOperationException(
        "The controlled connection document was not deleted by its owner.");
}

try
{
    _ = ApiHost.Build(new ApiRuntimeOptions(0, "not-a-canonical-api-token"));
    throw new InvalidOperationException(
        "The API accepted a non-canonical bearer token.");
}
catch (ArgumentException)
{
    // Production accepts only the 43-character base64url token it generates.
}

using var target = Process.Start(new ProcessStartInfo
{
    FileName = Environment.ProcessPath!,
    ArgumentList = { "--target", temporaryPath, eventName },
    UseShellExecute = false,
    RedirectStandardOutput = true,
    CreateNoWindow = true
}) ?? throw new InvalidOperationException("Could not start the controlled target process.");

try
{
    var ready = await target.StandardOutput.ReadLineAsync();
    if (ready != "READY")
    {
        throw new InvalidOperationException("The controlled target process did not become ready.");
    }

    var service = new HandleService();

    var boundedMatches = service.FindHandles(
        Environment.ProcessId,
        string.Empty,
        HandleMatchMode.Contains,
        progress: null,
        CancellationToken.None,
        includeUnnamed: true,
        resultFilter: static _ => true,
        maximumMatches: 2);
    if (boundedMatches.Count != 2)
    {
        throw new InvalidOperationException(
            "The handle scanner did not enforce its requested result cap.");
    }

    var fileMatches = service.FindHandles(
        target.Id,
        temporaryPath,
        HandleMatchMode.Exact,
        progress: null,
        CancellationToken.None);

    var fileMatch = fileMatches.SingleOrDefault(entry =>
        entry.Name.EndsWith(Path.GetFileName(temporaryPath), StringComparison.OrdinalIgnoreCase));

    if (fileMatch is null)
    {
        throw new InvalidOperationException("The controlled file handle was not discovered.");
    }

    if (fileMatch.ProcessCreationTimeUtcFileTime <= 0)
    {
        throw new InvalidOperationException(
            "The discovered handle did not carry its process creation time.");
    }

    var identityService = new ProcessIdentityService();
    var identity = identityService.GetIdentity(target.Id);
    var currentIdentity = identityService.GetIdentity(Environment.ProcessId);
    if (identity.ProcessId != target.Id ||
        !string.Equals(identity.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(identity.ImagePath) ||
        !identity.OwnerSid.StartsWith("S-", StringComparison.Ordinal) ||
        !string.Equals(identity.OwnerSid, currentIdentity.OwnerSid, StringComparison.Ordinal) ||
        identity.IsElevated != currentIdentity.IsElevated ||
        identity.WindowsSessionId != currentIdentity.WindowsSessionId ||
        identity.CreationTimeUtcFileTime != fileMatch.ProcessCreationTimeUtcFileTime)
    {
        throw new InvalidOperationException(
            "The controlled target process identity was incomplete or inconsistent.");
    }

    try
    {
        service.CloseHandle(fileMatch with
        {
            ProcessCreationTimeUtcFileTime =
                checked(fileMatch.ProcessCreationTimeUtcFileTime + 1)
        });
        throw new InvalidOperationException(
            "A handle with a mismatched process creation time was closed.");
    }
    catch (InvalidOperationException exception)
        when (exception.Message.Contains("different process", StringComparison.OrdinalIgnoreCase))
    {
        // The process-identity guard rejected a simulated PID reuse.
    }

    service.CloseHandle(fileMatch);

    using (new FileStream(
               temporaryPath,
               FileMode.Open,
               FileAccess.ReadWrite,
               FileShare.None))
    {
        // Opening exclusively proves that the target process no longer owns the file handle.
    }

    try
    {
        service.CloseHandle(fileMatch);
        throw new InvalidOperationException("A stale file handle was closed a second time.");
    }
    catch (InvalidOperationException exception)
        when (exception.Message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase))
    {
        // The stale-handle guard behaved as intended.
    }

    var commandTestHandle = new HandleEntry(
        ProcessId: 123,
        HandleValue: (nuint)0x2F0,
        ObjectAddress: (nuint)0x1234,
        GrantedAccess: RobloxAutomationRecipe.HandleAccess,
        ObjectType: RobloxAutomationRecipe.HandleType,
        Name: @"\Sessions\1\BaseNamedObjects\ROBLOX_singletonEvent",
        NativeName: @"\Sessions\1\BaseNamedObjects\ROBLOX_singletonEvent");
    var recurringCommand = AutomationCommandBuilder.BuildRecurringCloseCommand(
        RobloxAutomationRecipe.ProcessName,
        commandTestHandle);

    if (!recurringCommand.Contains(
            @"$env:LOCALAPPDATA\Programs\HandleScope\Api\Invoke-HandleScopeClose.ps1",
            StringComparison.Ordinal) ||
        !recurringCommand.Contains("-ProcessName 'RobloxPlayerBeta'", StringComparison.Ordinal) ||
        !recurringCommand.Contains(
            @"-HandleName '\Sessions\1\BaseNamedObjects\ROBLOX_singletonEvent'",
            StringComparison.Ordinal) ||
        !recurringCommand.Contains("-Type 'Event'", StringComparison.Ordinal) ||
        !recurringCommand.Contains("-Access '0x001F0003'", StringComparison.Ordinal) ||
        !recurringCommand.EndsWith(" -Exact -AllProcesses", StringComparison.Ordinal) ||
        recurringCommand.Contains("-ProcessId", StringComparison.Ordinal) ||
        recurringCommand.Contains("-HandleValue", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The recurring command was not stable or correctly escaped: {recurringCommand}");
    }

    VerifyRestrictedRobloxPolicy(currentIdentity);

    var installedRoblox = Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roblox",
        "Versions"))
        ? Directory.EnumerateFiles(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "Versions"),
                "RobloxPlayerBeta.exe",
                SearchOption.AllDirectories)
            .FirstOrDefault()
        : null;
    if (installedRoblox is not null &&
        !new RobloxExecutableVerifier().IsTrusted(installedRoblox))
    {
        throw new InvalidOperationException(
            "The offline Authenticode verifier rejected the installed official Roblox client.");
    }

    var eventMatch = service.FindHandles(
            target.Id,
            eventName,
            HandleMatchMode.Contains,
            progress: null,
            CancellationToken.None)
        .Single(entry =>
            entry.ObjectType.Equals("Event", StringComparison.OrdinalIgnoreCase));

    var apiToken = ConnectionFile.CreateToken();
    var controlledPolicy = new ControlledAutomationPolicy(identity, eventMatch);
    const string injectedEndpointVariable =
        "Kestrel__Endpoints__Injected__Url";
    var previousInjectedEndpoint = Environment.GetEnvironmentVariable(
        injectedEndpointVariable);
    Microsoft.AspNetCore.Builder.WebApplication api;
    try
    {
        Environment.SetEnvironmentVariable(
            injectedEndpointVariable,
            "http://0.0.0.0:0");
        api = ApiHost.Build(
            new ApiRuntimeOptions(0, apiToken, controlledPolicy));
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            injectedEndpointVariable,
            previousInjectedEndpoint);
    }

    await using (api)
    {
        await api.StartAsync();

        try
        {
            var apiUrl = api.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses
                .Single()
                ?? throw new InvalidOperationException("The test API did not publish an address.");

            using var client = new HttpClient { BaseAddress = new Uri(apiUrl) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiToken);

            var healthResponse = await client.GetAsync("/v1/health");
            var healthJson = await healthResponse.Content.ReadAsStringAsync();
            if (!healthResponse.IsSuccessStatusCode ||
                !healthJson.Contains(controlledPolicy.PolicyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The health response did not identify the active restricted policy.");
            }

            using (var unauthenticatedClient = new HttpClient { BaseAddress = new Uri(apiUrl) })
            {
                var unauthorizedResponse = await unauthenticatedClient.PostAsJsonAsync(
                    "/v1/handles/close",
                    CreateControlledRequest(target.Id, eventMatch, dryRun: true));
                if (unauthorizedResponse.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        "The API accepted a protected request without its bearer token.");
                }
            }

            using (var browserRequest = new HttpRequestMessage(
                       HttpMethod.Post,
                       "/v1/handles/close"))
            {
                browserRequest.Headers.Add("Origin", "http://127.0.0.1");
                browserRequest.Content = JsonContent.Create(
                    CreateControlledRequest(target.Id, eventMatch, dryRun: true));
                var browserResponse = await client.SendAsync(browserRequest);
                if (browserResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        "The API accepted a browser-originated protected request.");
                }
            }

            var strictJson = JsonSerializer.Serialize(
                CreateControlledRequest(target.Id, eventMatch, dryRun: true),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var strictDocument = JsonDocument.Parse(strictJson))
            {
                var unknownJson = strictJson[..^1] + ",\"dryRum\":true}";
                using var unknownFieldContent = new StringContent(
                    unknownJson,
                    System.Text.Encoding.UTF8,
                    "application/json");
                var unknownFieldResponse = await client.PostAsync(
                    "/v1/handles/close",
                    unknownFieldContent);
                if (unknownFieldResponse.StatusCode != System.Net.HttpStatusCode.BadRequest)
                {
                    throw new InvalidOperationException(
                        "The API accepted an unknown safety-field typo.");
                }

                var duplicateJson = strictJson[..^1] + ",\"dryRun\":false}";
                using var duplicateContent = new StringContent(
                    duplicateJson,
                    System.Text.Encoding.UTF8,
                    "application/json");
                var duplicateResponse = await client.PostAsync(
                    "/v1/handles/close",
                    duplicateContent);
                if (duplicateResponse.StatusCode != System.Net.HttpStatusCode.BadRequest)
                {
                    throw new InvalidOperationException(
                        "The API accepted a duplicate destructive safety field.");
                }

                var missingDryRun = strictJson.Replace(
                    "\"dryRun\":true,",
                    string.Empty,
                    StringComparison.Ordinal);
                if (missingDryRun == strictJson)
                {
                    throw new InvalidOperationException(
                        "The strict-request test could not remove dryRun.");
                }
                using var missingDryRunContent = new StringContent(
                    missingDryRun,
                    System.Text.Encoding.UTF8,
                    "application/json");
                var missingDryRunResponse = await client.PostAsync(
                    "/v1/handles/close",
                    missingDryRunContent);
                if (missingDryRunResponse.StatusCode != System.Net.HttpStatusCode.BadRequest)
                {
                    throw new InvalidOperationException(
                        "The API accepted a request with no explicit dryRun field.");
                }
            }

            using (var oversizedContent = new StringContent(
                       new string('x', (8 * 1024) + 1),
                       System.Text.Encoding.UTF8,
                       "application/json"))
            {
                using var oversizedClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
                oversizedClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiToken);
                try
                {
                    var oversizedResponse = await oversizedClient.PostAsync(
                        "/v1/handles/close",
                        oversizedContent);
                    if (oversizedResponse.StatusCode !=
                        System.Net.HttpStatusCode.RequestEntityTooLarge)
                    {
                        throw new InvalidOperationException(
                            "The API accepted an oversized request body.");
                    }
                }
                catch (HttpRequestException)
                {
                    // Kestrel may reset the loopback connection before reading an oversized body.
                }
            }

            var deniedSelector = CreateControlledRequest(
                target.Id,
                eventMatch,
                dryRun: true);
            deniedSelector = new CloseHandlesRequest
            {
                Process = deniedSelector.Process,
                Handle = new HandleSelector
                {
                    Name = deniedSelector.Handle!.Name,
                    Match = "contains",
                    Type = deniedSelector.Handle.Type,
                    Access = deniedSelector.Handle.Access
                },
                DryRun = true,
                CloseAll = false,
                AllProcesses = false
            };
            var deniedResponse = await client.PostAsJsonAsync(
                "/v1/handles/close",
                deniedSelector);
            if (deniedResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "A valid token broadened the installed automation policy or bypassed " +
                    $"strict handling: {(int)deniedResponse.StatusCode} " +
                    await deniedResponse.Content.ReadAsStringAsync());
            }

            var executeWithoutPlan = await client.PostAsJsonAsync(
                "/v1/handles/close",
                CreateControlledRequest(target.Id, eventMatch, dryRun: false));
            if (executeWithoutPlan.StatusCode != System.Net.HttpStatusCode.Conflict)
            {
                throw new InvalidOperationException(
                    "The API executed without a preceding dry-run plan.");
            }

            var dryRunResponse = await client.PostAsJsonAsync(
                "/v1/handles/close",
                CreateControlledRequest(target.Id, eventMatch, dryRun: true));
            var dryRunJson = await dryRunResponse.Content.ReadAsStringAsync();
            if (!dryRunResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The API selector dry run failed ({(int)dryRunResponse.StatusCode}): {dryRunJson}");
            }

            using (var dryRunDocument = JsonDocument.Parse(dryRunJson))
            {
                if (dryRunDocument.RootElement.GetProperty("matchCount").GetInt32() != 1 ||
                    dryRunDocument.RootElement.GetProperty("closedCount").GetInt32() != 0)
                {
                    throw new InvalidOperationException(
                        $"The restricted dry run returned an unexpected result: {dryRunJson}");
                }

                var match = dryRunDocument.RootElement
                    .GetProperty("matches")
                    .EnumerateArray()
                    .Single();
                if (match.GetProperty("handle").GetString() != "redacted" ||
                    match.GetProperty("object").GetString() != "redacted" ||
                    match.GetProperty("nativeName").GetString() != string.Empty ||
                    dryRunJson.Contains(apiToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The API response exposed a token, handle value, native name, or object address.");
                }
            }

            var closeResponse = await client.PostAsJsonAsync(
                "/v1/handles/close",
                CreateControlledRequest(target.Id, eventMatch, dryRun: false));
            var responseJson = await closeResponse.Content.ReadAsStringAsync();

            if (!closeResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The API close request failed ({(int)closeResponse.StatusCode}): {responseJson}");
            }

            using var responseDocument = JsonDocument.Parse(responseJson);
            if (responseDocument.RootElement.GetProperty("closedCount").GetInt32() != 1 ||
                responseDocument.RootElement.GetProperty("processCount").GetInt32() != 1)
            {
                throw new InvalidOperationException(
                    $"The API did not report exactly one closed handle: {responseJson}");
            }

            var replayResponse = await client.PostAsJsonAsync(
                "/v1/handles/close",
                CreateControlledRequest(target.Id, eventMatch, dryRun: false));
            if (replayResponse.StatusCode != System.Net.HttpStatusCode.Conflict)
            {
                throw new InvalidOperationException(
                    "The API replayed a consumed dry-run plan.");
            }
        }
        finally
        {
            await api.StopAsync();
        }
    }

    try
    {
        using var reopenedEvent = EventWaitHandle.OpenExisting(eventName);
        throw new InvalidOperationException("The named event still exists after its last handle was closed.");
    }
    catch (WaitHandleCannotBeOpenedException)
    {
        // The named kernel object disappeared when its last handle closed.
    }

    Console.WriteLine(
        $"PASS: closed file {fileMatch.HandleDisplay}; verified connection JSON, authentication, " +
        $"browser rejection, ambient-listener rejection, bounded results, strict JSON, policy confinement, " +
        $"single-use dry run, privacy redaction, " +
        $"process identity, PID-reuse rejection, and stale-handle rejection in controlled PID {target.Id}.");
}
finally
{
    if (!target.HasExited)
    {
        target.Kill(entireProcessTree: true);
        await target.WaitForExitAsync();
    }

    File.Delete(temporaryPath);
}

static CloseHandlesRequest CreateControlledRequest(
    int processId,
    HandleEntry handle,
    bool dryRun) =>
    new()
    {
        Process = new ProcessSelector { Pid = processId },
        Handle = new HandleSelector
        {
            Name = handle.Name,
            Match = "exact",
            Type = handle.ObjectType,
            Access = handle.AccessDisplay
        },
        DryRun = dryRun,
        CloseAll = false,
        AllProcesses = false
    };

static void VerifyRestrictedRobloxPolicy(ProcessIdentity currentIdentity)
{
    const int controlledPid = 4242;
    var brokerIdentity = currentIdentity with
    {
        ProcessId = Environment.ProcessId,
        IsElevated = false,
        WindowsSessionId = Math.Max(1, currentIdentity.WindowsSessionId),
        OwnerSid = "S-1-5-21-1000"
    };
    var approvedTarget = brokerIdentity with
    {
        ProcessId = controlledPid,
        ProcessName = RobloxSingletonAutomationPolicy.ProcessName,
        ImagePath = Path.Combine(
            Path.GetTempPath(),
            "Roblox",
            "Versions",
            "version-test",
            "RobloxPlayerBeta.exe"),
        CreationTimeUtcFileTime = brokerIdentity.CreationTimeUtcFileTime + 1
    };

    ProcessIdentity GetIdentity(int pid) => pid == Environment.ProcessId
        ? brokerIdentity
        : pid == controlledPid
            ? approvedTarget
            : throw new ArgumentOutOfRangeException(nameof(pid));

    var policy = new RobloxSingletonAutomationPolicy(
        GetIdentity,
        new FixedExecutableVerifier(isTrusted: true));
    var valid = CreateRobloxPolicyRequest(
        controlledPid,
        checked((int)brokerIdentity.WindowsSessionId));
    var authorizedRequest = policy.AuthorizeRequest(valid);
    if (!authorizedRequest.IsAllowed ||
        !policy.AuthorizeProcess(controlledPid, authorizedRequest).IsAllowed)
    {
        throw new InvalidOperationException(
            "The compiled policy rejected its one supported Roblox operation.");
    }

    var deniedRequests = new[]
    {
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), dryRun: null),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), match: "contains"),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), access: "0x1F0002"),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId + 1)),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), closeAll: true),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), rawHandle: "0x40")
    };
    if (deniedRequests.Any(request => policy.AuthorizeRequest(request).IsAllowed))
    {
        throw new InvalidOperationException(
            "The compiled Roblox policy accepted a broadened selector.");
    }

    var elevatedPolicy = new RobloxSingletonAutomationPolicy(
        pid => pid == Environment.ProcessId
            ? brokerIdentity
            : approvedTarget with { IsElevated = true },
        new FixedExecutableVerifier(isTrusted: true));
    var wrongOwnerPolicy = new RobloxSingletonAutomationPolicy(
        pid => pid == Environment.ProcessId
            ? brokerIdentity
            : approvedTarget with { OwnerSid = "S-1-5-21-2000" },
        new FixedExecutableVerifier(isTrusted: true));
    var unsignedPolicy = new RobloxSingletonAutomationPolicy(
        GetIdentity,
        new FixedExecutableVerifier(isTrusted: false));
    if (elevatedPolicy.AuthorizeProcess(controlledPid, authorizedRequest).IsAllowed ||
        wrongOwnerPolicy.AuthorizeProcess(controlledPid, authorizedRequest).IsAllowed ||
        unsignedPolicy.AuthorizeProcess(controlledPid, authorizedRequest).IsAllowed)
    {
        throw new InvalidOperationException(
            "The compiled policy accepted an elevated, foreign-owner, or untrusted target.");
    }

    if (!ApiRuntimeGuard.IsAllowed(brokerIdentity) ||
        ApiRuntimeGuard.IsAllowed(brokerIdentity with { IsElevated = true }) ||
        ApiRuntimeGuard.IsAllowed(brokerIdentity with { WindowsSessionId = 0 }) ||
        ApiRuntimeGuard.IsAllowed(brokerIdentity with { OwnerSid = "S-1-5-18" }))
    {
        throw new InvalidOperationException(
            "The API runtime guard accepted an elevated, service, or session-0 identity.");
    }
}

static CloseHandlesRequest CreateRobloxPolicyRequest(
    int processId,
    int sessionId,
    bool? dryRun = true,
    string match = "exact",
    string access = "0x001F0003",
    bool closeAll = false,
    string? rawHandle = null) =>
    new()
    {
        Process = new ProcessSelector { Pid = processId },
        Handle = new HandleSelector
        {
            Name = $@"\Sessions\{sessionId}\BaseNamedObjects\ROBLOX_singletonEvent",
            Match = match,
            Handle = rawHandle,
            Type = "Event",
            Access = access
        },
        DryRun = dryRun,
        CloseAll = closeAll,
        AllProcesses = false
    };

file sealed class ControlledAutomationPolicy(
    ProcessIdentity expectedIdentity,
    HandleEntry expectedHandle) : IHandleAutomationPolicy
{
    private readonly ProcessIdentityService _identityService = new();

    public string PolicyId => "controlled-test-v1";

    public int MaximumProcessCount => 1;

    public AutomationRequestAuthorization AuthorizeRequest(CloseHandlesRequest request)
    {
        if (request.Process?.Pid != expectedIdentity.ProcessId ||
            request.AllProcesses ||
            request.CloseAll ||
            request.DryRun is null ||
            request.Handle is null ||
            !string.IsNullOrEmpty(request.Handle.Handle) ||
            !string.Equals(request.Handle.Name, expectedHandle.Name, StringComparison.Ordinal) ||
            !string.Equals(request.Handle.Match, "exact", StringComparison.Ordinal) ||
            !string.Equals(request.Handle.Type, expectedHandle.ObjectType, StringComparison.Ordinal) ||
            !string.Equals(request.Handle.Access, expectedHandle.AccessDisplay, StringComparison.Ordinal))
        {
            return AutomationRequestAuthorization.Denied("policy_denied");
        }

        return new AutomationRequestAuthorization(
            true,
            checked((int)expectedIdentity.WindowsSessionId),
            $"controlled:{expectedIdentity.ProcessId}:{expectedHandle.Name}",
            string.Empty,
            expectedIdentity.ProcessName,
            expectedHandle.ObjectType,
            expectedHandle.GrantedAccess);
    }

    public AutomationProcessAuthorization AuthorizeProcess(
        int processId,
        AutomationRequestAuthorization request)
    {
        try
        {
            var current = _identityService.GetIdentity(processId);
            return request.IsAllowed &&
                   current.ProcessId == expectedIdentity.ProcessId &&
                   current.CreationTimeUtcFileTime == expectedIdentity.CreationTimeUtcFileTime &&
                   current.WindowsSessionId == expectedIdentity.WindowsSessionId &&
                   string.Equals(
                       current.OwnerSid,
                       expectedIdentity.OwnerSid,
                       StringComparison.Ordinal)
                ? new AutomationProcessAuthorization(true, current, string.Empty)
                : AutomationProcessAuthorization.Denied("policy_denied");
        }
        catch
        {
            return AutomationProcessAuthorization.Denied("policy_denied");
        }
    }
}

file sealed class FixedExecutableVerifier(bool isTrusted) : IRobloxExecutableVerifier
{
    public bool IsTrusted(string imagePath) => isTrusted;
}
