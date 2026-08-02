using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HandleScope.Api;
using HandleScope.Compatibility;
using HandleScope.Models;
using HandleScope.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

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

VerifyDryRunPlanIsolationAndExpiry();
VerifyAuthorizedProcessCap();
VerifyDeterministicExecutableVerifier();
VerifyApiCompatibilityPreferenceStore();

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

    var displayedSnapshot = new ProcessService(identityService)
        .GetProcessSnapshots()
        .SingleOrDefault(snapshot => snapshot.Row.ProcessId == target.Id);
    if (displayedSnapshot is null ||
        displayedSnapshot.Identity.CreationTimeUtcFileTime !=
            displayedSnapshot.Row.ProcessCreationTimeUtcFileTime ||
        !string.Equals(
            displayedSnapshot.Identity.ProcessName,
            displayedSnapshot.Row.Name,
            StringComparison.Ordinal) ||
        displayedSnapshot.Identity.CreationTimeUtcFileTime !=
            identity.CreationTimeUtcFileTime)
    {
        throw new InvalidOperationException(
            "The displayed process row was not paired with the identity that supplied its name.");
    }

    try
    {
        _ = service.FindHandles(
            target.Id,
            checked(identity.CreationTimeUtcFileTime + 1),
            temporaryPath,
            HandleMatchMode.Exact,
            progress: null,
            CancellationToken.None);
        throw new InvalidOperationException(
            "A handle scan accepted a mismatched process creation time.");
    }
    catch (InvalidOperationException exception)
        when (exception.Message.Contains("different process", StringComparison.OrdinalIgnoreCase))
    {
        // The identity-pinned scan rejected a simulated PID reuse before resolving handles.
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

    try
    {
        service.CloseHandle(fileMatch with
        {
            GrantedAccess = fileMatch.GrantedAccess ^ 1U
        });
        throw new InvalidOperationException(
            "A handle with mismatched access rights was closed.");
    }
    catch (InvalidOperationException exception)
        when (exception.Message.Contains("access rights changed", StringComparison.OrdinalIgnoreCase))
    {
        // The access-mask guard rejected a stale or substituted handle snapshot.
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
            using (var healthDocument = JsonDocument.Parse(healthJson))
            {
                var health = healthDocument.RootElement;
                if (health.GetPropertyCount() != 3 ||
                    health.GetProperty("status").GetString() != "ready" ||
                    health.GetProperty("apiVersion").GetString() != "v1" ||
                    health.GetProperty("policy").GetString() != controlledPolicy.PolicyId)
                {
                    throw new InvalidOperationException(
                        "The legacy v1 health response shape changed.");
                }
            }

            var v2HealthResponse = await client.GetAsync("/v2/health");
            using (var v2HealthDocument = JsonDocument.Parse(
                       await v2HealthResponse.Content.ReadAsStringAsync()))
            {
                var health = v2HealthDocument.RootElement;
                if (!v2HealthResponse.IsSuccessStatusCode ||
                    health.GetProperty("apiVersion").GetString() != "v2" ||
                    health.GetProperty("preferredApiVersion").GetString() != "v2" ||
                    !health.GetProperty("supportedApiVersions")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .SequenceEqual(new[] { "v1", "v2" }))
                {
                    throw new InvalidOperationException(
                        "The v2 health response did not advertise the reviewed adapters.");
                }
            }

            var metadataResponse = await client.GetAsync("/v1/metadata");
            var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
            using (var metadataDocument = JsonDocument.Parse(metadataJson))
            {
                var metadata = metadataDocument.RootElement;
                if (!metadataResponse.IsSuccessStatusCode ||
                    metadata.GetPropertyCount() != 7 ||
                    metadata.GetProperty("schemaVersion").GetInt32() != 1 ||
                    metadata.GetProperty("discoveryApiVersion").GetString() != "v1" ||
                    metadata.GetProperty("preferredApiVersion").GetString() != "v2" ||
                    !metadata.GetProperty("supportedApiVersions")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .SequenceEqual(new[] { "v1", "v2" }) ||
                    !metadata.GetProperty("policies")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .SequenceEqual(new[] { "controlled-test-v1" }) ||
                    !metadata.GetProperty("capabilities")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .SequenceEqual(new[]
                        {
                            "handlescope.http.v1",
                            "handlescope.http.v2",
                            "handlescope.plan.single-use.v1",
                            "handlescope.policy.roblox-singleton-event.v1"
                        }))
                {
                    throw new InvalidOperationException(
                        $"The authenticated API metadata response is incomplete: {metadataJson}");
                }
            }

            using (var unauthenticatedClient = new HttpClient { BaseAddress = new Uri(apiUrl) })
            {
                var metadataWithoutToken = await unauthenticatedClient.GetAsync(
                    "/v1/metadata");
                if (metadataWithoutToken.StatusCode !=
                    System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        "The API exposed compatibility metadata without its bearer token.");
                }

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

            using (var lookalikeJsonContent = new StringContent(
                       strictJson,
                       System.Text.Encoding.UTF8))
            {
                lookalikeJsonContent.Headers.ContentType =
                    MediaTypeHeaderValue.Parse("application/jsonp");
                var lookalikeJsonResponse = await client.PostAsync(
                    "/v1/handles/close",
                    lookalikeJsonContent);
                if (lookalikeJsonResponse.StatusCode !=
                    System.Net.HttpStatusCode.UnsupportedMediaType)
                {
                    throw new InvalidOperationException(
                        "The API accepted an application/json lookalike media type.");
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
            if (executeWithoutPlan.StatusCode != System.Net.HttpStatusCode.BadRequest)
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

            string planId;
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

                planId = dryRunDocument.RootElement
                    .GetProperty("planId")
                    .GetString()!;
                if (!DryRunPlanStore.IsCanonicalPlanId(planId))
                {
                    throw new InvalidOperationException(
                        "The API dry run did not issue a canonical single-use plan identifier.");
                }
            }

            var closeResponse = await client.PostAsJsonAsync(
                "/v2/handles/close",
                CreateControlledRequest(
                    target.Id,
                    eventMatch,
                    dryRun: false,
                    planId));
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
                CreateControlledRequest(
                    target.Id,
                    eventMatch,
                    dryRun: false,
                    planId));
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
        $"v1/v2 compatibility, browser rejection, ambient-listener rejection, bounded results, strict JSON, policy confinement, " +
        $"independent single-use plans, monotonic expiry, media-type confinement, trust policy, privacy redaction, " +
        $"authorized-process limits, complete responses, process identity, PID-reuse rejection, access-mask " +
        $"revalidation, and stale-handle rejection in controlled PID {target.Id}.");
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

static void VerifyApiCompatibilityPreferenceStore()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"HandleScope-compatibility-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "compatibility.json");
    try
    {
        var store = new ApiCompatibilityPreferenceStore(path);
        var missing = store.Read();
        if (!missing.IsValid || missing.Exists ||
            missing.Mode != ApiCompatibilityMode.Automatic)
        {
            throw new InvalidOperationException(
                "A missing compatibility preference did not select safe automatic mode.");
        }

        store.Write(ApiCompatibilityMode.V1);
        var legacy = store.Read();
        if (!legacy.IsValid || !legacy.Exists ||
            legacy.Mode != ApiCompatibilityMode.V1)
        {
            throw new InvalidOperationException(
                "The exact legacy compatibility preference did not round-trip.");
        }

        File.WriteAllText(
            path,
            "{\"schemaVersion\":1,\"mode\":\"v2\",\"unexpected\":true}");
        var invalid = store.Read();
        if (invalid.IsValid || !invalid.Exists ||
            invalid.Mode != ApiCompatibilityMode.Automatic)
        {
            throw new InvalidOperationException(
                "An extended compatibility preference was not rejected safely.");
        }
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* Test-only best effort. */ }
    }
}

static CloseHandlesRequest CreateControlledRequest(
    int processId,
    HandleEntry handle,
    bool dryRun,
    string? planId = null) =>
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
        AllProcesses = false,
        PlanId = planId
    };

static void VerifyDryRunPlanIsolationAndExpiry()
{
    var clock = new ManualTimeProvider(
        new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    var store = new DryRunPlanStore(clock);
    const string canonicalKey = "same-reviewed-operation";
    var firstPlanId = store.Put(canonicalKey, 1, 0, []);
    var secondPlanId = store.Put(canonicalKey, 2, 0, []);
    if (firstPlanId == secondPlanId ||
        !DryRunPlanStore.IsCanonicalPlanId(firstPlanId) ||
        !DryRunPlanStore.IsCanonicalPlanId(secondPlanId))
    {
        throw new InvalidOperationException(
            "Two dry runs for the same operation did not receive independent random plan identifiers.");
    }

    if (!store.TryTake(firstPlanId, canonicalKey, out var firstPlan) ||
        firstPlan?.ProcessCount != 1 ||
        !store.TryTake(secondPlanId, canonicalKey, out var secondPlan) ||
        secondPlan?.ProcessCount != 2)
    {
        throw new InvalidOperationException(
            "A later dry run overwrote another client's reviewed plan.");
    }

    var expiringPlanId = store.Put(canonicalKey, 1, 0, []);
    clock.MoveUtcBackward(TimeSpan.FromDays(1));
    clock.AdvanceTimestamp(TimeSpan.FromSeconds(6));
    if (store.TryTake(expiringPlanId, canonicalKey, out _))
    {
        throw new InvalidOperationException(
            "A dry-run plan survived its monotonic five-second lifetime after the wall clock moved backward.");
    }

    var identity = new ProcessIdentity(
        101,
        "RobloxPlayerBeta",
        @"C:\Roblox\Versions\version-test\RobloxPlayerBeta.exe",
        1,
        "S-1-5-21-1000",
        false,
        1);
    var handles = Enumerable.Range(1, 3)
        .Select(index => new HandleEntry(
            identity.ProcessId,
            (nuint)index,
            (nuint)(100 + index),
            RobloxAutomationRecipe.HandleAccess,
            RobloxAutomationRecipe.HandleType,
            GetRobloxHandleName(identity.WindowsSessionId),
            GetRobloxHandleName(identity.WindowsSessionId))
        {
            ProcessCreationTimeUtcFileTime = identity.CreationTimeUtcFileTime
        })
        .ToArray();
    var plannedMatches = ApiHost.GetPlannedMatches(new DryRunPlan(
        new string('A', 43),
        canonicalKey,
        clock.GetTimestamp(),
        1,
        1,
        0,
        [new AuthorizedProcessPlan(identity, handles)]));
    if (plannedMatches.Length != handles.Length)
    {
        throw new InvalidOperationException(
            "The execution response silently truncated reviewed matches.");
    }
}

static void VerifyAuthorizedProcessCap()
{
    var request = new CloseHandlesRequest
    {
        Process = new ProcessSelector { Name = RobloxAutomationRecipe.ProcessName },
        Handle = new HandleSelector
        {
            Name = GetRobloxHandleName(1),
            Match = "exact",
            Type = RobloxAutomationRecipe.HandleType,
            Access = $"0x{RobloxAutomationRecipe.HandleAccess:X8}"
        },
        DryRun = true,
        AllProcesses = true,
        CloseAll = false
    };
    var authorization = new AutomationRequestAuthorization(
        true,
        1,
        "candidate-cap-test",
        string.Empty,
        RobloxAutomationRecipe.ProcessName,
        RobloxAutomationRecipe.HandleType,
        RobloxAutomationRecipe.HandleAccess);
    var candidates = Enumerable.Range(1, 64).ToArray();
    var mostlyUnauthorized = new CandidateAutomationPolicy(
        new HashSet<int> { 64 },
        32);
    var filtered = ApiHost.AuthorizeCandidateProcesses(
        candidates,
        request,
        authorization,
        mostlyUnauthorized);
    if (filtered.TooMany || filtered.SelectedPidDenied ||
        filtered.Authorized.Count != 1 ||
        filtered.Authorized[0].ProcessId != 64)
    {
        throw new InvalidOperationException(
            "Unauthorized lookalike processes consumed the authorized-process safety cap.");
    }

    var overLimit = ApiHost.AuthorizeCandidateProcesses(
        Enumerable.Range(1, 33),
        request,
        authorization,
        new CandidateAutomationPolicy(
            Enumerable.Range(1, 33).ToHashSet(),
            maximumProcessCount: 32));
    if (!overLimit.TooMany)
    {
        throw new InvalidOperationException(
            "The API did not enforce its cap after authorizing too many target processes.");
    }
}

static void VerifyDeterministicExecutableVerifier()
{
    var testRoot = Path.Combine(
        Path.GetTempPath(),
        $"HandleScope-verifier-{Guid.NewGuid():N}");
    var versionsRoot = Path.Combine(testRoot, "Roblox", "Versions");
    var executablePath = Path.Combine(
        versionsRoot,
        "version-test",
        "RobloxPlayerBeta.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
    File.WriteAllText(executablePath, "controlled verifier input");

    try
    {
        var trustServices = new ControlledExecutableTrustServices(executablePath);
        var verifier = new RobloxExecutableVerifier([versionsRoot], trustServices);
        if (!verifier.IsTrusted(executablePath))
        {
            throw new InvalidOperationException(
                "The concrete Roblox verifier rejected a fully approved controlled executable.");
        }

        trustServices.ContainsReparsePointResult = true;
        AssertVerifierRejects(verifier, executablePath, "a reparse-point path");
        trustServices.ContainsReparsePointResult = false;

        trustServices.HasExpectedVersionIdentityResult = false;
        AssertVerifierRejects(verifier, executablePath, "invalid version metadata");
        trustServices.HasExpectedVersionIdentityResult = true;

        trustServices.IsSignedAndTrustedResult = false;
        AssertVerifierRejects(verifier, executablePath, "a failed WinVerifyTrust result");
        trustServices.IsSignedAndTrustedResult = true;

        trustServices.HasExpectedSignerResult = false;
        AssertVerifierRejects(verifier, executablePath, "the wrong signer identity");
        trustServices.HasExpectedSignerResult = true;

        trustServices.CanonicalPath = Path.Combine(
            versionsRoot,
            "not-a-version",
            "RobloxPlayerBeta.exe");
        AssertVerifierRejects(verifier, executablePath, "an invalid version directory");
        trustServices.CanonicalPath = executablePath;

        trustServices.ThrowOnCanonicalPath = true;
        AssertVerifierRejects(verifier, executablePath, "a canonical-path failure");

        var windowsTrustServices =
            new RobloxExecutableVerifier.WindowsExecutableTrustServices();
        using (var executableStream = new FileStream(
                   executablePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            if (windowsTrustServices.IsSignedAndTrusted(
                    executablePath,
                    executableStream.SafeFileHandle))
            {
                throw new InvalidOperationException(
                    "WinVerifyTrust accepted an unsigned controlled executable.");
            }
        }

        var signerRejected = false;
        try
        {
            signerRejected = !windowsTrustServices.HasExpectedSigner(executablePath);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            signerRejected = true;
        }

        if (!signerRejected)
        {
            throw new InvalidOperationException(
                "The production signer check accepted an unsigned controlled executable.");
        }

        var productionPrimitiveVerifier = new RobloxExecutableVerifier(
            [versionsRoot],
            windowsTrustServices);
        if (productionPrimitiveVerifier.IsTrusted(executablePath))
        {
            throw new InvalidOperationException(
                "The concrete Windows trust primitives accepted an unsigned controlled executable.");
        }
    }
    finally
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void AssertVerifierRejects(
    RobloxExecutableVerifier verifier,
    string executablePath,
    string scenario)
{
    if (verifier.IsTrusted(executablePath))
    {
        throw new InvalidOperationException(
            $"The concrete Roblox verifier accepted {scenario}.");
    }
}

static string GetRobloxHandleName(uint sessionId) =>
    $@"\Sessions\{sessionId}\BaseNamedObjects\ROBLOX_singletonEvent";

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

    var validExecution = CreateRobloxPolicyRequest(
        controlledPid,
        checked((int)brokerIdentity.WindowsSessionId),
        dryRun: false,
        planId: new string('A', 43));
    if (!policy.AuthorizeRequest(validExecution).IsAllowed)
    {
        throw new InvalidOperationException(
            "The compiled policy rejected an execution bound to a canonical dry-run plan identifier.");
    }

    var deniedRequests = new[]
    {
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), dryRun: null),
        CreateRobloxPolicyRequest(controlledPid, checked((int)brokerIdentity.WindowsSessionId), dryRun: false),
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
    string? rawHandle = null,
    string? planId = null) =>
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
        AllProcesses = false,
        PlanId = planId
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

file sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void MoveUtcBackward(TimeSpan amount) => _utcNow -= amount;

    public void AdvanceTimestamp(TimeSpan amount) =>
        _timestamp = checked(_timestamp + amount.Ticks);
}

file sealed class CandidateAutomationPolicy(
    IReadOnlySet<int> allowedProcessIds,
    int maximumProcessCount) : IHandleAutomationPolicy
{
    public string PolicyId => "candidate-cap-test-v1";

    public int MaximumProcessCount => maximumProcessCount;

    public AutomationRequestAuthorization AuthorizeRequest(
        CloseHandlesRequest request) =>
        new(
            true,
            1,
            "candidate-cap-test",
            string.Empty,
            RobloxAutomationRecipe.ProcessName,
            RobloxAutomationRecipe.HandleType,
            RobloxAutomationRecipe.HandleAccess);

    public AutomationProcessAuthorization AuthorizeProcess(
        int processId,
        AutomationRequestAuthorization request) =>
        allowedProcessIds.Contains(processId)
            ? new AutomationProcessAuthorization(
                true,
                new ProcessIdentity(
                    processId,
                    RobloxAutomationRecipe.ProcessName,
                    $@"C:\Roblox\Versions\version-{processId}\RobloxPlayerBeta.exe",
                    1,
                    "S-1-5-21-1000",
                    false,
                    processId),
                string.Empty)
            : AutomationProcessAuthorization.Denied("policy_denied");
}

file sealed class ControlledExecutableTrustServices(string canonicalPath) :
    IRobloxExecutableTrustServices
{
    public string CanonicalPath { get; set; } = canonicalPath;

    public bool ThrowOnCanonicalPath { get; set; }

    public bool ContainsReparsePointResult { get; set; }

    public bool HasExpectedVersionIdentityResult { get; set; } = true;

    public bool IsSignedAndTrustedResult { get; set; } = true;

    public bool HasExpectedSignerResult { get; set; } = true;

    public string GetCanonicalPath(SafeFileHandle handle) =>
        ThrowOnCanonicalPath
            ? throw new IOException("Controlled canonical-path failure.")
            : CanonicalPath;

    public bool ContainsReparsePoint(string root, string path) =>
        ContainsReparsePointResult;

    public bool HasExpectedVersionIdentity(string path) =>
        HasExpectedVersionIdentityResult;

    public bool IsSignedAndTrusted(string path, SafeFileHandle fileHandle) =>
        IsSignedAndTrustedResult;

    public bool HasExpectedSigner(string path) => HasExpectedSignerResult;
}
