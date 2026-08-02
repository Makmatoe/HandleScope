using System.Security.Cryptography;
using System.Text;
using HandleScope.Api;
using HandleScope.Compatibility;
using HandleScope.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var processId = Environment.ProcessId;
var identityService = new ProcessIdentityService();
var identity = identityService.GetIdentity(processId);
if (!ApiRuntimeGuard.IsAllowed(identity))
{
    Environment.ExitCode = 5;
    return;
}

using var instanceSemaphore = new Semaphore(
    initialCount: 1,
    maximumCount: 1,
    ApiRuntimeGuard.GetInstanceName(identity),
    out _);
if (!instanceSemaphore.WaitOne(0))
{
    return;
}

var connectionPath = ConnectionFile.DefaultPath;
var token = ConnectionFile.CreateToken();
var compatibilityPreference = new ApiCompatibilityPreferenceStore().Read();
var compatibilityMode = compatibilityPreference.IsValid
    ? compatibilityPreference.Mode
    : ApiCompatibilityMode.Automatic;

try
{
    await using var app = ApiHost.Build(
        new ApiRuntimeOptions(
            ConnectionFile.DefaultPort,
            token,
            CompatibilityMode: compatibilityMode));
    await app.StartAsync();

    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()
        ?.Addresses;
    var baseUrl = addresses?.SingleOrDefault()
        ?? throw new InvalidOperationException("The local API address is unavailable.");

    ConnectionFile.Write(
        connectionPath,
        new ApiConnection(
            "v1",
            baseUrl.TrimEnd('/'),
            token,
            processId,
            DateTimeOffset.UtcNow));
    ConnectionFile.AppendLog(
        "API started in restricted standard-user mode with preferred " +
        $"{ApiCompatibilityPolicy.Resolve(compatibilityMode)} compatibility.");

    await app.WaitForShutdownAsync();
}
catch (Exception exception)
{
    ConnectionFile.AppendLog(
        $"API stopped after {exception.GetType().Name} (0x{exception.HResult:X8}).");
    Environment.ExitCode = 1;
}
finally
{
    ConnectionFile.DeleteIfOwned(connectionPath, processId);
    ConnectionFile.AppendLog("API stopped.");
    instanceSemaphore.Release();
}

public static class ApiRuntimeGuard
{
    private static readonly HashSet<string> ServiceAccounts =
    [
        "S-1-5-18",
        "S-1-5-19",
        "S-1-5-20"
    ];

    public static bool IsAllowed(HandleScope.Models.ProcessIdentity identity) =>
        !identity.IsElevated &&
        identity.WindowsSessionId != 0 &&
        !ServiceAccounts.Contains(identity.OwnerSid);

    public static string GetInstanceName(HandleScope.Models.ProcessIdentity identity)
    {
        var sidHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.OwnerSid));
        return $@"Local\HandleScope.Api.{Convert.ToHexString(sidHash.AsSpan(0, 8))}." +
               identity.WindowsSessionId;
    }
}
