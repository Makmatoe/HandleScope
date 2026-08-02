using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HandleScope.Services;
using HandleScope.Setup;

if (args is ["--lock-attempt", var lockSid])
{
    try
    {
        using var unexpected = SetupMutationLock.Acquire(
            lockSid,
            TimeSpan.FromMilliseconds(600));
        Console.Error.WriteLine("The contended setup lock was acquired unexpectedly.");
        Environment.ExitCode = 19;
    }
    catch (SetupOperationException)
    {
        Environment.ExitCode = 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        Environment.ExitCode = 20;
    }
    return;
}

if (args is ["--failed-launch-child"])
{
    using var hold = new ManualResetEventSlim(initialState: false);
    hold.Wait(TimeSpan.FromMinutes(2));
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("strict command grammar", TestCommandGrammar),
    ("standard interactive identity", TestIdentityPolicy),
    ("fixed LocalAppData paths", TestPathPolicy),
    ("exact locked bundle", TestBundleVerification),
    ("bundle mutation detection", TestBundleMutation),
    ("hardlink rejection", TestHardlinkRejection),
    ("alternate stream policy", TestAlternateStreams),
    ("directory inventory policy", TestDirectoryInventory),
    ("runtime identity policy", TestRuntimeIdentity),
    ("transaction recovery", TestTransactionRecovery),
    ("cross-session mutation lock", TestMutationLock),
    ("failed launch exact cleanup", TestFailedLaunchCleanup),
    ("native install option boundaries", TestNativeInstall),
    ("SessionDock setting policy", TestSessionDockIntegration),
    ("limited autostart XML", TestAutostartXml),
    ("maintenance target boundary", TestMaintenanceBoundary),
    ("legacy wrapper mapping", TestLegacyWrappers),
    ("no runtime shell dependency", TestNoRuntimeShellDependency),
    ("Restricted policy native launch", TestRestrictedPolicyLaunch)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"HandleScope native setup tests passed ({tests.Length}/{tests.Length}).");

static void TestCommandGrammar()
{
    var install = SetupCommand.Parse(
        [
            "install",
            "--enable-autostart",
            "--start-now",
            "--enable-sessiondock",
            "--allow-downgrade"
        ]);
    Assert(
        install.Verb == SetupVerb.Install && install.StartNow &&
        install.EnableAutostart && install.EnableSessionDock &&
        install.AllowDowngrade,
        "Install options were not mapped exactly.");
    Assert(SetupCommand.Parse(["verify"]).Verb == SetupVerb.Verify,
        "Verify was not parsed.");
    Assert(SetupCommand.Parse(["enable-sessiondock", "--force"]).Force,
        "Force was not parsed.");
    Assert(
        SetupCommand.Parse(["uninstall", "--keep-diagnostics"])
            .KeepDiagnostics,
        "Keep diagnostics was not parsed.");
    AssertThrows<SetupUsageException>(() => SetupCommand.Parse([]));
    AssertThrows<SetupUsageException>(() => SetupCommand.Parse(["Install"]));
    AssertThrows<SetupUsageException>(() =>
        SetupCommand.Parse(["install", "--start-now", "--start-now"]));
    AssertThrows<SetupUsageException>(() =>
        SetupCommand.Parse(["verify", "--start-now"]));
    AssertThrows<SetupUsageException>(() =>
        SetupCommand.Parse(["uninstall", "--force"]));
}

static void TestIdentityPolicy()
{
    new SetupIdentity("DOMAIN\\user", "S-1-5-21-1", 1, false)
        .AssertStandardInteractiveUser();
    AssertThrows<SetupSafetyException>(() =>
        new SetupIdentity("DOMAIN\\user", "S-1-5-21-1", 1, true)
            .AssertStandardInteractiveUser());
    AssertThrows<SetupSafetyException>(() =>
        new SetupIdentity("SYSTEM", "S-1-5-18", 0, false)
            .AssertStandardInteractiveUser());
    AssertThrows<SetupSafetyException>(() =>
        new SetupIdentity("DOMAIN\\user", "S-1-5-21-1", 0, false)
            .AssertStandardInteractiveUser());
}

static void TestPathPolicy()
{
    using var root = new TemporaryDirectory("paths");
    var local = Path.Combine(root.Path, "Local");
    Directory.CreateDirectory(local);
    var paths = new SetupPaths(local);
    paths.ValidateRoot();
    paths.CreateDirectory(paths.ProductRoot);
    Assert(Directory.Exists(paths.ProductRoot), "Fixed product path was not created.");
    AssertThrows<SetupSafetyException>(() =>
        paths.AssertContained(Path.Combine(root.Path, "outside")));
}

static void TestBundleVerification()
{
    using var fixture = BundleFixture.Create();
    using var lease = BundleLease.Acquire(fixture.SetupPath);
    Assert(lease.Runtime.Version == new Version(0, 3, 0),
        "Runtime version was not authenticated.");
    lease.Revalidate();

    var staging = Path.Combine(fixture.OperationRoot, "staging");
    Directory.CreateDirectory(staging);
    lease.CopyApiFiles(staging);
    using var installed = BundleLease.AcquireInstalledApiLease(staging);
    lease.AssertApiSnapshotMatchesSource(installed.Snapshot);
}

static void TestBundleMutation()
{
    using var fixture = BundleFixture.Create();
    using var lease = BundleLease.Acquire(fixture.SetupPath);
    var unexpected = Path.Combine(fixture.BundleRoot, "unexpected.txt");
    File.WriteAllText(unexpected, "changed after verification");
    AssertThrows<SetupSafetyException>(lease.Revalidate);
}

static void TestHardlinkRejection()
{
    using var fixture = BundleFixture.Create();
    var source = Path.Combine(fixture.BundleRoot, "api", "API.md");
    var link = Path.Combine(fixture.OperationRoot, "outside-hardlink.md");
    if (!CreateHardLink(link, source, IntPtr.Zero))
    {
        throw new InvalidOperationException(
            $"Could not create controlled hardlink: {Marshal.GetLastWin32Error()}");
    }
    AssertThrows<SetupSafetyException>(() =>
    {
        using var ignored = BundleLease.Acquire(fixture.SetupPath);
    });
}

static void TestAlternateStreams()
{
    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        File.WriteAllText(
            marked + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/\r\n");
        File.Delete(marked + ":mshield");
        using var lease = BundleLease.Acquire(fixture.SetupPath);
        var staging = Path.Combine(fixture.OperationRoot, "marked-staging");
        Directory.CreateDirectory(staging);
        lease.CopyApiFiles(staging);
        AssertThrows<FileNotFoundException>(() =>
        {
            using var ignored = File.OpenRead(
                Path.Combine(staging, "API.md") + ":Zone.Identifier");
        });
        AssertThrows<FileNotFoundException>(() =>
        {
            using var ignored = File.OpenRead(
                Path.Combine(staging, "API.md") + ":mshield");
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        File.WriteAllText(marked + ":mshield", "bounded endpoint metadata");
        File.WriteAllText(marked + ":endpoint-metadata", "bounded unknown metadata");
        using var lease = BundleLease.Acquire(fixture.SetupPath);
        var staging = Path.Combine(fixture.OperationRoot, "mshield-staging");
        Directory.CreateDirectory(staging);
        lease.CopyApiFiles(staging);
        AssertThrows<FileNotFoundException>(() =>
        {
            using var ignored = File.OpenRead(
                Path.Combine(staging, "API.md") + ":mshield");
        });
        AssertThrows<FileNotFoundException>(() =>
        {
            using var ignored = File.OpenRead(
                Path.Combine(staging, "API.md") + ":endpoint-metadata");
        });
        File.WriteAllText(
            Path.Combine(staging, "API.md") + ":residual",
            "not allowed after staging");
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.AcquireInstalledApiLease(staging);
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        File.WriteAllText(marked + ":Zone.Identifier", "arbitrary bytes");
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.Acquire(fixture.SetupPath);
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        File.WriteAllText(marked + ":oversized", new string('x', 64 * 1024 + 1));
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.Acquire(fixture.SetupPath);
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        File.WriteAllText(marked + ":bad name", "malformed name");
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.Acquire(fixture.SetupPath);
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        for (var index = 0; index < 9; index++)
        {
            File.WriteAllText(marked + $":meta{index}", "x");
        }
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.Acquire(fixture.SetupPath);
        });
    }

    using (var fixture = BundleFixture.Create())
    {
        var marked = Path.Combine(fixture.BundleRoot, "api", "API.md");
        for (var index = 0; index < 3; index++)
        {
            File.WriteAllText(marked + $":total{index}", new string('x', 50 * 1024));
        }
        AssertThrows<SetupSafetyException>(() =>
        {
            using var ignored = BundleLease.Acquire(fixture.SetupPath);
        });
    }
}

static void TestDirectoryInventory()
{
    using var fixture = BundleFixture.Create();
    Directory.CreateDirectory(Path.Combine(fixture.BundleRoot, "empty"));
    AssertThrows<SetupSafetyException>(() =>
    {
        using var ignored = BundleLease.Acquire(fixture.SetupPath);
    });
}

static void TestRuntimeIdentity()
{
    using var fixture = BundleFixture.Create();
    fixture.WriteRuntime(schemaVersion: 1, includeNativeCapability: false);
    fixture.WriteManifest();
    AssertThrows<SetupSafetyException>(() =>
    {
        using var ignored = BundleLease.Acquire(fixture.SetupPath);
    });
}

static void TestTransactionRecovery()
{
    static void WriteJournal(
        SetupPaths paths,
        string phase,
        bool hadExisting,
        string? treeDigest = null)
    {
        if (phase != "preparing" && treeDigest is null)
        {
            treeDigest = new string('0', 64);
        }
        File.WriteAllText(
            paths.TransactionPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                phase,
                hadExisting,
                treeDigest
            }) + "\n");
    }

    using (var root = new TemporaryDirectory("recover-preparing"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.ProductRoot);
        paths.CreateDirectory(paths.StagingRoot);
        File.WriteAllText(Path.Combine(paths.StagingRoot, "partial"), "partial");
        WriteJournal(paths, "preparing", hadExisting: false);
        new InstallationTransaction(paths).Recover();
        Assert(!Directory.Exists(paths.StagingRoot),
            "Preparing recovery did not remove contained staging data.");
        Assert(!File.Exists(paths.TransactionPath),
            "Preparing recovery did not clear its journal.");
    }

    using (var root = new TemporaryDirectory("recover-untracked"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.StagingRoot);
        AssertThrows<SetupSafetyException>(() =>
            new InstallationTransaction(paths).Recover());
    }


    using (var root = new TemporaryDirectory("recover-staged-old"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.InstallRoot);
        File.WriteAllText(Path.Combine(paths.InstallRoot, "old"), "old");
        paths.CreateDirectory(paths.StagingRoot);
        File.WriteAllText(Path.Combine(paths.StagingRoot, "new"), "new");
        WriteJournal(paths, "staged", hadExisting: true);
        new InstallationTransaction(paths).Recover();
        Assert(File.Exists(Path.Combine(paths.InstallRoot, "old")) &&
            !Directory.Exists(paths.StagingRoot),
            "Staged recovery changed the untouched old installation.");
    }

    using (var root = new TemporaryDirectory("recover-after-backup"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.BackupRoot);
        File.WriteAllText(Path.Combine(paths.BackupRoot, "old"), "old");
        paths.CreateDirectory(paths.StagingRoot);
        File.WriteAllText(Path.Combine(paths.StagingRoot, "new"), "new");
        paths.CreateDirectory(paths.ProductRoot);
        WriteJournal(paths, "staged", hadExisting: true);
        new InstallationTransaction(paths).Recover();
        Assert(File.Exists(Path.Combine(paths.InstallRoot, "old")) &&
            !Directory.Exists(paths.BackupRoot) &&
            !Directory.Exists(paths.StagingRoot),
            "Recovery did not restore the backup after the first rename.");
    }

    using (var fixture = BundleFixture.Create())
    using (var root = new TemporaryDirectory("recover-after-promotion"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.InstallRoot);
        using (var bundle = BundleLease.Acquire(fixture.SetupPath))
        {
            bundle.CopyApiFiles(paths.InstallRoot);
        }
        paths.CreateDirectory(paths.BackupRoot);
        File.WriteAllText(Path.Combine(paths.BackupRoot, "old"), "old");
        string digest;
        using (var installed =
               BundleLease.AcquireInstalledApiLease(paths.InstallRoot))
        {
            digest = installed.Snapshot.CanonicalDigest;
        }
        WriteJournal(
            paths,
            "staged",
            hadExisting: true,
            treeDigest: digest);
        new InstallationTransaction(paths).Recover();
        Assert(Directory.Exists(paths.InstallRoot) &&
            !Directory.Exists(paths.BackupRoot) &&
            !File.Exists(paths.TransactionPath),
            "Recovery did not authenticate and commit the promoted tree.");
    }

    using (var fixture = BundleFixture.Create())
    using (var root = new TemporaryDirectory("recover-first-promoted"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.InstallRoot);
        using (var bundle = BundleLease.Acquire(fixture.SetupPath))
        {
            bundle.CopyApiFiles(paths.InstallRoot);
        }
        string digest;
        using (var installed =
               BundleLease.AcquireInstalledApiLease(paths.InstallRoot))
        {
            digest = installed.Snapshot.CanonicalDigest;
        }
        WriteJournal(
            paths,
            "staged",
            hadExisting: false,
            treeDigest: digest);
        new InstallationTransaction(paths).Recover();
        Assert(Directory.Exists(paths.InstallRoot) &&
            !File.Exists(paths.TransactionPath),
            "First-install promotion recovery did not converge.");
    }

    using (var root = new TemporaryDirectory("recover-first-staged"))
    {
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var paths = new SetupPaths(local);
        paths.CreateDirectory(paths.StagingRoot);
        File.WriteAllText(Path.Combine(paths.StagingRoot, "new"), "new");
        WriteJournal(paths, "staged", hadExisting: false);
        new InstallationTransaction(paths).Recover();
        Assert(!Directory.Exists(paths.StagingRoot) &&
            !File.Exists(paths.TransactionPath),
            "First-install staged recovery did not safely abort.");
    }
}

static void TestNativeInstall()
{
    using var fixture = BundleFixture.Create();
    using var local = new TemporaryDirectory("install");
    var localData = Path.Combine(local.Path, "Local");
    Directory.CreateDirectory(localData);
    var paths = new SetupPaths(localData);
    var lifecycle = new FakeLifecycle();
    var autostart = new FakeAutostart();
    var integration = new FakeIntegration();
    var installer = new SetupInstaller(
        paths,
        lifecycle,
        autostart,
        integration);

    using (var bundle = BundleLease.Acquire(fixture.SetupPath))
    {
        installer.Install(bundle, new SetupCommand(SetupVerb.Install));
    }
    Assert(Directory.Exists(paths.InstallRoot), "Native install did not promote files.");
    Assert(lifecycle.Starts == 0 && integration.Enables == 0 &&
        autostart.State == AutostartState.Absent,
        "Install silently enabled an optional action.");
    using (var installed = BundleLease.AcquireInstalledApiLease(paths.InstallRoot))
    {
        Assert(installed.Runtime.Version == new Version(0, 3, 0),
            "Installed runtime identity was wrong.");
    }

    using (var bundle = BundleLease.Acquire(fixture.SetupPath))
    {
        installer.Install(
            bundle,
            new SetupCommand(
                SetupVerb.Install,
                StartNow: true,
                EnableAutostart: true,
                EnableSessionDock: true));
    }
    Assert(lifecycle.Stops == 1 && lifecycle.Starts == 1,
        "Explicit lifecycle options were not honored exactly.");
    Assert(autostart.State == AutostartState.Enabled,
        "Explicit autostart was not enabled.");
    Assert(integration.Enables == 1,
        "Explicit SessionDock integration was not enabled.");
}

static void TestMutationLock()
{
    const string sid = "S-1-5-21-1000-2000-3000-1001";
    var name = SetupMutationLock.GetNameForSid(sid);
    Assert(name.StartsWith("Global\\HandleScope.Setup.v1.", StringComparison.Ordinal),
        "Setup mutation lock is not cross-session.");
    Assert(!name.Contains(sid, StringComparison.Ordinal) &&
        name == SetupMutationLock.GetNameForSid(sid) &&
        name != SetupMutationLock.GetNameForSid(sid + "-2"),
        "Setup mutation lock is not stably and privately scoped per SID.");

    using (SetupMutationLock.Acquire(sid, TimeSpan.FromSeconds(2)))
    {
        var info = CreateSelfProcessStartInfo("--lock-attempt", sid);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException(
                "Could not start setup lock contention helper.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert(process.WaitForExit(10_000),
            "Setup lock contention helper did not terminate.");
        Assert(process.ExitCode == 0,
            $"A second process bypassed or could not safely open the global lock: {output} {error}");
    }

    using var reacquired = SetupMutationLock.Acquire(sid, TimeSpan.Zero);
}

static void TestFailedLaunchCleanup()
{
    var identities = new ProcessIdentityService();

    using (var exact = StartFailedLaunchChild())
    {
        var captured = identities.GetIdentity(exact.Id);
        ApiLifecycle.TerminateFailedLaunch(
            exact,
            captured,
            identities.GetIdentity,
            TimeSpan.FromSeconds(5));
        Assert(exact.HasExited,
            "The exact failed launch was not cleaned up.");
    }

    using (var refused = StartFailedLaunchChild())
    {
        var captured = identities.GetIdentity(refused.Id);
        var mismatched = captured with
        {
            CreationTimeUtcFileTime = captured.CreationTimeUtcFileTime + 1
        };
        AssertThrows<SetupSafetyException>(() =>
            ApiLifecycle.TerminateFailedLaunch(
                refused,
                captured,
                _ => mismatched,
                TimeSpan.FromSeconds(5)));
        Assert(!refused.HasExited,
            "Cleanup terminated a process after identity reauthentication failed.");
        ApiLifecycle.TerminateFailedLaunch(
            refused,
            captured,
            identities.GetIdentity,
            TimeSpan.FromSeconds(5));
    }

    using (var exited = StartFailedLaunchChild())
    {
        var captured = identities.GetIdentity(exited.Id);
        exited.Kill(entireProcessTree: false);
        Assert(exited.WaitForExit(5_000),
            "Controlled failed-launch child did not exit.");
        ApiLifecycle.TerminateFailedLaunch(
            exited,
            captured,
            _ => throw new InvalidOperationException(
                "An exited process must not be resolved by PID."),
            TimeSpan.FromSeconds(5));
    }
}

static Process StartFailedLaunchChild()
{
    var process = Process.Start(
        CreateSelfProcessStartInfo("--failed-launch-child"))
        ?? throw new InvalidOperationException(
            "Could not start controlled failed-launch child.");
    _ = process.SafeHandle;
    return process;
}

static void TestSessionDockIntegration()
{
    using var root = new TemporaryDirectory("sessiondock");
    var local = Path.Combine(root.Path, "Local");
    Directory.CreateDirectory(local);
    var paths = new SetupPaths(local);
    var integration = new SessionDockIntegration(paths);
    integration.Enable(force: false);
    Assert(integration.IsMinimalEnabled(paths.SessionDockSettingsPath),
        "Minimal SessionDock setting was not written.");

    File.WriteAllText(
        paths.SessionDockSettingsPath,
        "{\"enabled\":false,\"unexpected\":true}");
    AssertThrows<SetupSafetyException>(() => integration.Enable(force: false));
    integration.Enable(force: true);
    Assert(integration.IsMinimalEnabled(paths.SessionDockSettingsPath),
        "Forced explicit replacement did not write the minimal opt-in.");

    File.WriteAllText(paths.SessionDockSettingsPath, "true");
    AssertThrows<SetupSafetyException>(() => integration.Enable(force: false));
}

static void TestAutostartXml()
{
    using var root = new TemporaryDirectory("task");
    var executable = Path.Combine(root.Path, "HandleScope.Api.exe");
    var identity = new SetupIdentity(
        "DOMAIN\\user",
        "S-1-5-21-1000",
        1,
        false);
    var manager = new TaskSchedulerAutostartManager(identity);
    var xml = manager.CreateTaskXml(executable, root.Path);
    manager.ValidateTaskXmlWithScheduler(xml);
    Assert(manager.IsExpectedTask(xml, executable, root.Path),
        "Compiled task XML did not pass exact inspection.");
    Assert(xml.Contains(
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
            StringComparison.Ordinal) &&
        xml.Contains(
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
            StringComparison.Ordinal),
        "Native autostart is not explicitly laptop-safe on battery power.");
    Assert(!manager.IsExpectedTask(
            xml.Replace(
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
                "<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>",
                StringComparison.Ordinal),
            executable,
            root.Path),
        "A native task that refuses battery start was accepted.");
    Assert(!manager.IsExpectedTask(
            xml.Replace(
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
                string.Empty,
                StringComparison.Ordinal),
            executable,
            root.Path),
        "A native task with an implicit battery-stop default was accepted.");
    foreach (var mutation in new (string Expected, string Replacement)[]
             {
                 ("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
                     "<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>"),
                 ("<StartWhenAvailable>true</StartWhenAvailable>",
                     "<StartWhenAvailable>false</StartWhenAvailable>"),
                 ("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>",
                     "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>"),
                 ("<Interval>PT1M</Interval>", "<Interval>PT2M</Interval>"),
                 ("<Count>3</Count>", "<Count>4</Count>")
             })
    {
        Assert(!manager.IsExpectedTask(
                xml.Replace(
                    mutation.Expected,
                    mutation.Replacement,
                    StringComparison.Ordinal),
                executable,
                root.Path),
            $"An altered task behavior was accepted: {mutation.Replacement}");
    }
    Assert(!manager.IsExpectedTask(
            xml.Replace("LeastPrivilege", "HighestAvailable", StringComparison.Ordinal),
            executable,
            root.Path),
        "An elevated task was accepted.");
    Assert(!manager.IsExpectedTask(
            xml.Replace(
                "<WorkingDirectory>",
                "<Arguments>--unexpected</Arguments><WorkingDirectory>",
                StringComparison.Ordinal),
            executable,
            root.Path),
        "Task arguments were accepted.");
    var sidElement = $"<UserId>{identity.Sid}</UserId>";
    var firstSid = xml.IndexOf(sidElement, StringComparison.Ordinal);
    Assert(firstSid >= 0, "Generated task XML has no trigger identity.");
    var legacyXml = xml.Remove(firstSid, sidElement.Length)
        .Insert(firstSid, $"<UserId>{identity.Name}</UserId>")
        .Replace(
            "<RunLevel>LeastPrivilege</RunLevel>",
            string.Empty,
            StringComparison.Ordinal)
        .Replace(
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
            "<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>",
            StringComparison.Ordinal)
        .Replace(
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
            "<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>",
            StringComparison.Ordinal);
    Assert(manager.IsExpectedTask(legacyXml, executable, root.Path),
        "The exact legacy limited-current-user task was not accepted.");
    var legacyImplicitBatteryXml = legacyXml
        .Replace(
            "<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>",
            string.Empty,
            StringComparison.Ordinal)
        .Replace(
            "<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>",
            string.Empty,
            StringComparison.Ordinal);
    Assert(manager.IsExpectedTask(
            legacyImplicitBatteryXml,
            executable,
            root.Path),
        "The real legacy task battery defaults were not accepted for migration.");
    var settingsStart = legacyXml.IndexOf("<Settings>", StringComparison.Ordinal);
    var settingsEnd = legacyXml.IndexOf(
        "</Settings>",
        settingsStart,
        StringComparison.Ordinal);
    Assert(settingsStart >= 0 && settingsEnd > settingsStart,
        "Legacy task fixture has no settings element.");
    var legacyWithoutSettings = legacyXml.Remove(
        settingsStart,
        settingsEnd + "</Settings>".Length - settingsStart);
    Assert(!manager.IsExpectedTask(
            legacyWithoutSettings,
            executable,
            root.Path),
        "A legacy task without the required behavior settings was accepted.");
    Assert(!manager.IsExpectedTask(
            legacyXml.Replace(identity.Name, "DOMAIN\\other", StringComparison.Ordinal),
            executable,
            root.Path),
        "A legacy task owned by another identity was accepted.");
    var disabledXml = legacyXml.Replace(
        "<Enabled>true</Enabled>",
        "<Enabled>false</Enabled>",
        StringComparison.Ordinal);
    Assert(manager.IsExpectedTask(disabledXml, executable, root.Path) &&
        TaskSchedulerAutostartManager.IsTaskXmlDisabled(disabledXml),
        "A structurally exact disabled legacy task was not preserved.");
    Assert(!manager.IsExpectedTask(
            legacyXml.Replace(
                "<Enabled>true</Enabled>",
                "<Enabled>0</Enabled>",
                StringComparison.Ordinal),
            executable,
            root.Path),
        "A non-canonical enabled value was accepted.");
}

static void TestMaintenanceBoundary()
{
    using var root = new TemporaryDirectory("maintenance");
    var local = Path.Combine(root.Path, "Local");
    Directory.CreateDirectory(local);
    var paths = new SetupPaths(local);
    var launcher = new MaintenanceLauncher(
        paths,
        new SetupIdentity("DOMAIN\\user", "S-1-5-21-1", 1, false));
    Assert(launcher.RequiresMaintenanceCopy(paths.InstalledSetupPath),
        "Installed setup was not routed through maintenance.");
    Assert(!launcher.RequiresMaintenanceCopy(
            Path.Combine(root.Path, "download", "HandleScope.Setup.exe")),
        "An extracted setup was incorrectly treated as installed.");
    AssertThrows<SetupSafetyException>(() =>
        paths.AssertContained(Path.Combine(root.Path, "redirect-target")));

    var source = Path.Combine(root.Path, "source.exe");
    var destinationRoot = Path.Combine(root.Path, "destination");
    Directory.CreateDirectory(destinationRoot);
    var destination = Path.Combine(destinationRoot, "HandleScope.Setup.exe");
    var replacement = Path.Combine(destinationRoot, "replacement.exe");
    File.WriteAllText(source, "verified setup bytes");
    File.WriteAllText(replacement, "replacement bytes");
    var hookRan = false;
    MaintenanceLauncher.WithVerifiedCopyLocked(source, destination, path =>
    {
        hookRan = true;
        AssertFileMutationDenied(() => File.WriteAllText(path, "mutated"));
        AssertFileMutationDenied(() =>
            File.Move(replacement, path, overwrite: true));
    });
    Assert(hookRan && File.ReadAllText(destination) == "verified setup bytes",
        "The verified maintenance image was not locked through launch handoff.");
}

static void TestLegacyWrappers()
{
    var repository = FindRepositoryRoot();
    var scripts = new Dictionary<string, string[]>
    {
        ["Install-HandleScopeApi.ps1"] = ["'install'", "'verify'", "--start-now", "--enable-autostart", "--enable-sessiondock", "--allow-downgrade"],
        ["Start-HandleScopeApi.ps1"] = ["'start'"],
        ["Stop-HandleScopeApi.ps1"] = ["'stop'"],
        ["Uninstall-HandleScopeApi.ps1"] = ["'uninstall'", "--keep-diagnostics"],
        ["Enable-SessionDockIntegration.ps1"] = ["'enable-sessiondock'", "--force"]
    };
    foreach (var entry in scripts)
    {
        var source = File.ReadAllText(Path.Combine(
            repository,
            "HandleScope.Api",
            "Scripts",
            entry.Key));
        Assert(source.Contains("HandleScope.Setup.exe", StringComparison.Ordinal),
            $"{entry.Key} does not delegate to native setup.");
        foreach (var marker in entry.Value)
        {
            Assert(source.Contains(marker, StringComparison.Ordinal),
                $"{entry.Key} is missing exact mapping {marker}.");
        }
        Assert(!source.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase) &&
            !source.Contains("Set-ExecutionPolicy", StringComparison.OrdinalIgnoreCase),
            $"{entry.Key} contains policy manipulation.");
    }
}

static void TestNoRuntimeShellDependency()
{
    var setupRoot = Path.Combine(FindRepositoryRoot(), "HandleScope.Setup");
    var forbidden = new[]
    {
        "powershell.exe",
        "pwsh.exe",
        "cmd.exe",
        "ExecutionPolicy",
        "Set-ExecutionPolicy",
        "UseShellExecute = true"
    };
    foreach (var file in Directory.EnumerateFiles(setupRoot, "*.cs", SearchOption.AllDirectories))
    {
        var source = File.ReadAllText(file);
        foreach (var marker in forbidden)
        {
            Assert(!source.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"Native setup source depends on forbidden shell behavior: {marker}");
        }
    }
}

static void TestRestrictedPolicyLaunch()
{
    var repository = FindRepositoryRoot();
    var executable = Path.Combine(
        repository,
        "HandleScope.Setup",
        "bin",
        "Release",
        "net10.0-windows",
        "HandleScope.Setup.exe");
    Assert(File.Exists(executable), "Built native setup apphost is missing.");
    var powershell = Path.Combine(
        Environment.SystemDirectory,
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
    var info = new ProcessStartInfo
    {
        FileName = powershell,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    info.ArgumentList.Add("-NoLogo");
    info.ArgumentList.Add("-NoProfile");
    info.ArgumentList.Add("-NonInteractive");
    info.ArgumentList.Add("-ExecutionPolicy");
    info.ArgumentList.Add("Restricted");
    info.ArgumentList.Add("-Command");
    info.ArgumentList.Add(
        "& $env:HANDLESCOPE_RESTRICTED_TEST_EXE 'invalid-command'; exit $LASTEXITCODE");
    info.Environment["HANDLESCOPE_RESTRICTED_TEST_EXE"] = executable;
    using var process = Process.Start(info)
        ?? throw new InvalidOperationException("Could not start Restricted policy test.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert(process.ExitCode == 2,
        $"Native setup did not execute under Restricted policy: {output} {error}");
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "HandleScope.slnx")))
        {
            return directory.FullName;
        }
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Could not locate the HandleScope repository.");
}

static ProcessStartInfo CreateSelfProcessStartInfo(params string[] arguments)
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException(
            "The setup test process path is unavailable.");
    var info = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
    {
        info.ArgumentList.Add(
            System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException(
                "The setup test assembly path is unavailable."));
    }
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }
    return info;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static T AssertThrows<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }
    throw new InvalidOperationException(
        $"Expected {typeof(T).Name} was not thrown.");
}

static void AssertFileMutationDenied(Action action)
{
    try
    {
        action();
    }
    catch (IOException)
    {
        return;
    }
    catch (UnauthorizedAccessException)
    {
        return;
    }
    throw new InvalidOperationException(
        "A locked verified maintenance executable was mutable.");
}

sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory(string description)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"HandleScope-SetupTests-{description}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

sealed class BundleFixture : IDisposable
{
    private BundleFixture(
        TemporaryDirectory temporary,
        string bundleRoot,
        string setupPath)
    {
        Temporary = temporary;
        BundleRoot = bundleRoot;
        SetupPath = setupPath;
    }

    internal TemporaryDirectory Temporary { get; }
    internal string OperationRoot => Temporary.Path;
    internal string BundleRoot { get; }
    internal string SetupPath { get; }

    internal static BundleFixture Create()
    {
        var repository = FindBundleRepositoryRoot();
        var temporary = new TemporaryDirectory("bundle");
        var bundle = Path.Combine(temporary.Path, "HandleScope-0.3.0-win-x64");
        var api = Path.Combine(bundle, "api");
        Directory.CreateDirectory(api);
        Directory.CreateDirectory(Path.Combine(bundle, "desktop"));
        Directory.CreateDirectory(Path.Combine(bundle, "docs"));

        var setupSource = Path.Combine(
            repository,
            "HandleScope.Setup",
            "bin",
            "Release",
            "net10.0-windows",
            "HandleScope.Setup.exe");
        var apiSource = Path.Combine(
            repository,
            "HandleScope.Api",
            "bin",
            "Release",
            "net10.0-windows",
            "HandleScope.Api.exe");
        if (!File.Exists(setupSource) || !File.Exists(apiSource))
        {
            temporary.Dispose();
            throw new InvalidOperationException(
                "Release apphosts must be built before running Setup tests.");
        }
        var setup = Path.Combine(api, "HandleScope.Setup.exe");
        File.Copy(setupSource, setup);
        File.Copy(apiSource, Path.Combine(api, "HandleScope.Api.exe"));
        foreach (var name in BundleLease.GetInstalledFileNames())
        {
            var path = Path.Combine(api, name);
            if (!File.Exists(path) && name != "HandleScope.runtime.json")
            {
                File.WriteAllText(path, $"controlled {name}\n");
            }
        }
        File.WriteAllText(Path.Combine(bundle, "README.md"), "controlled root\n");
        File.WriteAllText(
            Path.Combine(bundle, "desktop", "HandleScope.exe"),
            "controlled desktop\n");
        File.WriteAllText(
            Path.Combine(bundle, "docs", "INSTALL.md"),
            "controlled docs\n");

        var fixture = new BundleFixture(temporary, bundle, setup);
        fixture.WriteRuntime(schemaVersion: 2, includeNativeCapability: true);
        fixture.WriteManifest();
        return fixture;
    }

    internal void WriteRuntime(int schemaVersion, bool includeNativeCapability)
    {
        var capabilities = new List<string>
        {
            "handlescope.http.v1",
            "handlescope.http.v2",
            "handlescope.plan.single-use.v1",
            "handlescope.policy.roblox-singleton-event.v1"
        };
        if (includeNativeCapability)
        {
            capabilities.Add("handlescope.setup.native.v1");
        }
        var runtime = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["product"] = "HandleScope.Api",
            ["repository"] = "Makmatoe/HandleScope",
            ["version"] = "0.3.0",
            ["tag"] = "v0.3.0",
            ["sourceRevision"] = new string('0', 40),
            ["sourceTimestamp"] = "2026-08-02T00:00:00.0000000+00:00",
            ["runtime"] = "win-x64",
            ["discoveryApiVersion"] = "v1",
            ["supportedApiVersions"] = new[] { "v1", "v2" },
            ["preferredApiVersion"] = "v2",
            ["policies"] = new[] { "roblox-singleton-event-v1" },
            ["capabilities"] = capabilities
        };
        File.WriteAllText(
            Path.Combine(BundleRoot, "api", "HandleScope.runtime.json"),
            JsonSerializer.Serialize(
                runtime,
                new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal void WriteManifest()
    {
        var manifest = Path.Combine(BundleRoot, "CONTENTS.sha256");
        if (File.Exists(manifest))
        {
            File.Delete(manifest);
        }
        var lines = Directory.EnumerateFiles(
                BundleRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Relative = Path.GetRelativePath(BundleRoot, path).Replace('\\', '/')
            })
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item =>
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Path))).ToLowerInvariant()}  {item.Relative}");
        File.WriteAllText(
            manifest,
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose() => Temporary.Dispose();

    private static string FindBundleRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HandleScope.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the HandleScope repository for bundle tests.");
    }
}

sealed class FakeLifecycle : IApiLifecycle
{
    internal int Starts { get; private set; }
    internal int Stops { get; private set; }
    public void Start() => Starts++;
    public void Stop() => Stops++;
}

sealed class FakeAutostart : IAutostartManager
{
    internal AutostartState State { get; private set; } = AutostartState.Absent;
    public AutostartState Inspect(string executablePath, string workingDirectory) =>
        State;
    public void Enable(string executablePath, string workingDirectory) =>
        State = AutostartState.Enabled;
    public void Remove(string executablePath, string workingDirectory) =>
        State = AutostartState.Absent;
}

sealed class FakeIntegration : ISessionDockIntegration
{
    internal int Enables { get; private set; }
    public void Enable(bool force) => Enables++;
}

partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
