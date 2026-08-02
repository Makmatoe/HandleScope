namespace HandleScope.Setup;

internal sealed class SetupApplication
{
    private readonly SetupPaths _paths;
    private readonly SetupIdentity _identity;
    private readonly IApiLifecycle _lifecycle;
    private readonly IAutostartManager _autostart;
    private readonly ISessionDockIntegration _integration;
    private readonly IMaintenanceLauncher _maintenance;
    private readonly Func<string, BundleLease> _acquireBundle;
    private readonly string _currentExecutablePath;

    internal SetupApplication(
        SetupPaths paths,
        SetupIdentity identity,
        IApiLifecycle lifecycle,
        IAutostartManager autostart,
        ISessionDockIntegration integration,
        IMaintenanceLauncher maintenance,
        Func<string, BundleLease> acquireBundle,
        string currentExecutablePath)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _acquireBundle = acquireBundle ?? throw new ArgumentNullException(nameof(acquireBundle));
        _currentExecutablePath = SetupPaths.Normalize(currentExecutablePath);
    }

    internal static SetupApplication CreateDefault(
        SetupPaths paths,
        SetupIdentity identity,
        string currentExecutablePath)
    {
        var lifecycle = new ApiLifecycle(paths, identity);
        var autostart = new TaskSchedulerAutostartManager(identity);
        var integration = new SessionDockIntegration(paths);
        return new SetupApplication(
            paths,
            identity,
            lifecycle,
            autostart,
            integration,
            new MaintenanceLauncher(paths, identity),
            BundleLease.Acquire,
            currentExecutablePath);
    }

    internal void Run(SetupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _identity.AssertStandardInteractiveUser();
        _paths.ValidateRoot();
        if (command.Verb == SetupVerb.Verify)
        {
            using var bundle = _acquireBundle(_currentExecutablePath);
            bundle.Revalidate();
            Console.WriteLine(
                "HandleScope native setup verified the exact release inventory and hashes.");
            return;
        }

        using var mutationLock = SetupMutationLock.Acquire(_identity.Sid);
        var installer = new SetupInstaller(
            _paths,
            _lifecycle,
            _autostart,
            _integration);
        switch (command.Verb)
        {
            case SetupVerb.Install:
                using (var bundle = _acquireBundle(_currentExecutablePath))
                {
                    installer.Install(bundle, command);
                }
                break;
            case SetupVerb.Start:
                _lifecycle.Start();
                break;
            case SetupVerb.Stop:
                _lifecycle.Stop();
                break;
            case SetupVerb.EnableSessionDock:
                _integration.Enable(command.Force);
                break;
            case SetupVerb.Uninstall:
                if (_maintenance.RequiresMaintenanceCopy(_currentExecutablePath))
                {
                    using (BundleLease.AcquireInstalledApiLease(
                               _paths.InstallRoot))
                    {
                        _maintenance.LaunchUninstall(
                            _currentExecutablePath,
                            command.KeepDiagnostics);
                    }
                }
                else
                {
                    installer.Uninstall(command.KeepDiagnostics);
                }
                break;
            default:
                throw new SetupUsageException(
                    "The setup command is unsupported.");
        }
    }
}
