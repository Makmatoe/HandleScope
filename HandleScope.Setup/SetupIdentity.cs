using HandleScope.Services;

namespace HandleScope.Setup;

internal sealed record SetupIdentity(
    string Name,
    string Sid,
    uint SessionId,
    bool IsElevated)
{
    private static readonly IReadOnlySet<string> ServiceSids =
        new HashSet<string>(
            ["S-1-5-18", "S-1-5-19", "S-1-5-20"],
            StringComparer.Ordinal);

    internal static SetupIdentity Current()
    {
        var identity = new ProcessIdentityService().GetIdentity(
            Environment.ProcessId);
        return new SetupIdentity(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            identity.OwnerSid,
            identity.WindowsSessionId,
            identity.IsElevated);
    }

    internal void AssertStandardInteractiveUser()
    {
        if (IsElevated)
        {
            throw new SetupSafetyException(
                "HandleScope setup is per-user. Run it from a normal, non-administrator window.");
        }
        if (SessionId == 0 || ServiceSids.Contains(Sid))
        {
            throw new SetupSafetyException(
                "HandleScope setup requires an interactive standard-user session.");
        }
        if (string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(Sid) ||
            !Sid.StartsWith("S-", StringComparison.Ordinal))
        {
            throw new SetupSafetyException(
                "Windows did not provide a valid current-user identity.");
        }
    }
}
