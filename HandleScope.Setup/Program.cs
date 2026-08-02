using HandleScope.Setup;

const string usage = """
Usage:
  HandleScope.Setup.exe verify
  HandleScope.Setup.exe install [--start-now] [--enable-autostart] [--enable-sessiondock] [--allow-downgrade]
  HandleScope.Setup.exe start
  HandleScope.Setup.exe stop
  HandleScope.Setup.exe enable-sessiondock [--force]
  HandleScope.Setup.exe uninstall [--keep-diagnostics]
""";

try
{
    var command = SetupCommand.Parse(args);
    var executablePath = Environment.ProcessPath
        ?? throw new SetupSafetyException(
            "Windows did not provide the native setup executable path.");
    var localApplicationData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);
    var paths = new SetupPaths(localApplicationData);
    var identity = SetupIdentity.Current();
    identity.AssertStandardInteractiveUser();
    paths.ValidateRoot();
    MaintenanceLauncher.WaitForAuthenticatedParent(paths, identity);
    MaintenanceLauncher.CleanupStaleCopies(paths, executablePath);
    var application = SetupApplication.CreateDefault(
        paths,
        identity,
        executablePath);
    application.Run(command);
    Environment.ExitCode = 0;
}
catch (SetupUsageException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(usage);
    Environment.ExitCode = 2;
}
catch (SetupSafetyException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 3;
}
catch (SetupOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 4;
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        $"HandleScope setup failed safely ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
    Environment.ExitCode = 4;
}
finally
{
    if (Environment.ProcessPath is { } path &&
        !string.IsNullOrWhiteSpace(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData)))
    {
        try
        {
            MaintenanceLauncher.TryDeleteCurrentMaintenanceCopy(
                new SetupPaths(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData)),
                path);
        }
        catch
        {
            // A stale verified helper is retried by the next setup invocation.
        }
    }
}
