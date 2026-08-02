namespace HandleScope.Setup;

internal enum SetupVerb
{
    Verify,
    Install,
    Start,
    Stop,
    EnableSessionDock,
    Uninstall
}

internal sealed record SetupCommand(
    SetupVerb Verb,
    bool StartNow = false,
    bool EnableAutostart = false,
    bool EnableSessionDock = false,
    bool AllowDowngrade = false,
    bool Force = false,
    bool KeepDiagnostics = false)
{
    internal static SetupCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new SetupUsageException("A setup command is required.");
        }

        var verb = arguments[0] switch
        {
            "verify" => SetupVerb.Verify,
            "install" => SetupVerb.Install,
            "start" => SetupVerb.Start,
            "stop" => SetupVerb.Stop,
            "enable-sessiondock" => SetupVerb.EnableSessionDock,
            "uninstall" => SetupVerb.Uninstall,
            _ => throw new SetupUsageException(
                $"Unknown setup command: {arguments[0]}")
        };

        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (!options.Add(option))
            {
                throw new SetupUsageException(
                    $"A setup option was supplied more than once: {option}");
            }
        }

        var allowed = verb switch
        {
            SetupVerb.Install => new HashSet<string>(
                [
                    "--start-now",
                    "--enable-autostart",
                    "--enable-sessiondock",
                    "--allow-downgrade"
                ],
                StringComparer.Ordinal),
            SetupVerb.EnableSessionDock => new HashSet<string>(
                ["--force"],
                StringComparer.Ordinal),
            SetupVerb.Uninstall => new HashSet<string>(
                ["--keep-diagnostics"],
                StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
        var unexpected = options.FirstOrDefault(option => !allowed.Contains(option));
        if (unexpected is not null)
        {
            throw new SetupUsageException(
                $"Option {unexpected} is not valid for {arguments[0]}.");
        }

        return new SetupCommand(
            verb,
            options.Contains("--start-now"),
            options.Contains("--enable-autostart"),
            options.Contains("--enable-sessiondock"),
            options.Contains("--allow-downgrade"),
            options.Contains("--force"),
            options.Contains("--keep-diagnostics"));
    }
}

internal sealed class SetupUsageException(string message) : Exception(message);

internal sealed class SetupSafetyException : Exception
{
    internal SetupSafetyException(string message)
        : base(message)
    {
    }

    internal SetupSafetyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class SetupOperationException : Exception
{
    internal SetupOperationException(string message)
        : base(message)
    {
    }

    internal SetupOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
