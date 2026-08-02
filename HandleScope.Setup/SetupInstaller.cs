using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace HandleScope.Setup;

internal sealed class SetupInstaller
{
    private readonly SetupPaths _paths;
    private readonly IApiLifecycle _lifecycle;
    private readonly IAutostartManager _autostart;
    private readonly ISessionDockIntegration _integration;
    private readonly InstallationTransaction _transaction;

    internal SetupInstaller(
        SetupPaths paths,
        IApiLifecycle lifecycle,
        IAutostartManager autostart,
        ISessionDockIntegration integration)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
        _transaction = new InstallationTransaction(paths);
    }

    internal void Install(BundleLease bundle, SetupCommand command)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (command.Verb != SetupVerb.Install)
        {
            throw new ArgumentException(
                "The setup command is not an installation.",
                nameof(command));
        }
        _transaction.Recover();
        var previousAutostart = _autostart.Inspect(
            _paths.InstalledApiPath,
            _paths.InstallRoot);
        if (previousAutostart == AutostartState.Unexpected)
        {
            throw new SetupSafetyException(
                "An unexpected task occupies the HandleScope autostart identity.");
        }

        if (Directory.Exists(_paths.InstallRoot))
        {
            _paths.AssertTreeSafe(_paths.InstallRoot);
            var installedVersion = ReadInstalledVersion();
            if (!command.AllowDowngrade && bundle.Runtime.Version < installedVersion)
            {
                throw new SetupSafetyException(
                    $"Setup refused to downgrade HandleScope from {installedVersion} to {bundle.Runtime.Version}.");
            }
            _lifecycle.Stop();
        }
        else if (File.Exists(_paths.InstallRoot))
        {
            throw new SetupSafetyException(
                "The HandleScope installation path is occupied by a file.");
        }

        bundle.Revalidate();
        _transaction.Replace(bundle);
        if (command.EnableAutostart)
        {
            using var installedLease =
                BundleLease.AcquireInstalledApiLease(_paths.InstallRoot);
            _autostart.Enable(_paths.InstalledApiPath, _paths.InstallRoot);
        }
        else if (previousAutostart is AutostartState.Enabled or AutostartState.Disabled)
        {
            var preserved = _autostart.Inspect(
                _paths.InstalledApiPath,
                _paths.InstallRoot);
            if (preserved != previousAutostart)
            {
                throw new SetupOperationException(
                    "The existing autostart task state was not preserved.");
            }
        }

        if (command.StartNow)
        {
            _lifecycle.Start();
        }
        if (command.EnableSessionDock)
        {
            _integration.Enable(force: false);
        }

        Console.WriteLine(
            $"HandleScope API {bundle.Runtime.VersionText} installed for the current user at {_paths.InstallRoot}");
        var autostart = _autostart.Inspect(
            _paths.InstalledApiPath,
            _paths.InstallRoot);
        Console.WriteLine(autostart switch
        {
            AutostartState.Enabled => command.EnableAutostart
                ? "Per-user standard-privilege autostart enabled."
                : "Per-user standard-privilege autostart remains enabled.",
            AutostartState.Disabled =>
                "The existing HandleScope autostart task remains disabled.",
            AutostartState.Absent =>
                "Autostart remains disabled; enable it only through an explicit install option.",
            _ => throw new SetupSafetyException(
                "The installed autostart state became unexpected.")
        });
    }

    internal void Uninstall(bool keepDiagnostics)
    {
        _transaction.Recover();
        var task = _autostart.Inspect(
            _paths.InstalledApiPath,
            _paths.InstallRoot);
        if (task == AutostartState.Unexpected)
        {
            throw new SetupSafetyException(
                "Setup refused to remove an unexpected HandleScope task.");
        }
        if (Directory.Exists(_paths.InstallRoot))
        {
            _lifecycle.Stop();
        }
        _autostart.Remove(_paths.InstalledApiPath, _paths.InstallRoot);
        _paths.DeleteTreeIfPresent(_paths.InstallRoot);
        _paths.DeleteTreeIfPresent(_paths.StagingRoot);
        _paths.DeleteTreeIfPresent(_paths.BackupRoot);
        _paths.DeleteFileIfPresent(_paths.TransactionPath);
        if (!keepDiagnostics)
        {
            _paths.DeleteTreeIfPresent(_paths.RuntimeRoot);
        }
        Console.WriteLine(
            "HandleScope API autostart, files, and requested local runtime data were removed.");
    }

    private Version ReadInstalledVersion()
    {
        var manifestPath = _paths.InstalledRuntimeManifestPath;
        var executablePath = _paths.InstalledApiPath;
        if (!File.Exists(manifestPath) || !File.Exists(executablePath))
        {
            throw new SetupSafetyException(
                "The existing HandleScope installation has no authentic version identity.");
        }
        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            var properties = root.EnumerateObject().ToArray();
            var expected = new HashSet<string>(
                [
                    "schemaVersion", "product", "repository", "version", "tag",
                    "sourceRevision", "sourceTimestamp", "runtime",
                    "discoveryApiVersion", "supportedApiVersions",
                    "preferredApiVersion", "policies", "capabilities"
                ],
                StringComparer.Ordinal);
            var schema = root.GetProperty("schemaVersion").GetInt32();
            var versionText = root.GetProperty("version").GetString();
            if (properties.Length != expected.Count ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != expected.Count ||
                !properties.Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
                schema is not (1 or 2) ||
                root.GetProperty("product").GetString() != "HandleScope.Api" ||
                root.GetProperty("repository").GetString() != "Makmatoe/HandleScope" ||
                root.GetProperty("runtime").GetString() != "win-x64" ||
                versionText is null ||
                !Version.TryParse(versionText, out var manifestVersion) ||
                manifestVersion.Build < 0 || manifestVersion.Revision >= 0 ||
                root.GetProperty("tag").GetString() != $"v{versionText}")
            {
                throw new SetupSafetyException(
                    "The existing HandleScope runtime manifest is invalid.");
            }
            var fileVersionText = FileVersionInfo
                .GetVersionInfo(executablePath).FileVersion;
            if (!TryNormalizeVersion(fileVersionText, out var fileVersion) ||
                fileVersion != manifestVersion)
            {
                throw new SetupSafetyException(
                    "The installed API executable and runtime manifest versions disagree.");
            }
            return manifestVersion;
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                InvalidOperationException or FormatException or OverflowException)
        {
            throw new SetupSafetyException(
                "The existing HandleScope version identity could not be authenticated.",
                exception);
        }
    }

    internal static bool TryNormalizeVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!Version.TryParse(text, out var parsed) || parsed.Build < 0 ||
            parsed.Revision is < -1 or > 0)
        {
            return false;
        }
        version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        return true;
    }
}
