namespace HandleScope.Setup;

internal sealed class SetupPaths
{
    internal SetupPaths(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        LocalApplicationData = Normalize(localApplicationData);
        if (!Path.IsPathFullyQualified(LocalApplicationData))
        {
            throw new SetupSafetyException(
                "Windows did not provide an absolute Local Application Data path.");
        }
        ProgramsRoot = Path.Combine(LocalApplicationData, "Programs");
        ProductRoot = Path.Combine(ProgramsRoot, "HandleScope");
        InstallRoot = Path.Combine(ProductRoot, "Api");
        StagingRoot = Path.Combine(ProductRoot, "Api.staging");
        BackupRoot = Path.Combine(ProductRoot, "Api.backup");
        TransactionPath = Path.Combine(ProductRoot, "Api.transaction");
        RuntimeRoot = Path.Combine(LocalApplicationData, "HandleScope");
        ConnectionPath = Path.Combine(RuntimeRoot, "connection.json");
        LogPath = Path.Combine(RuntimeRoot, "api.log");
        SessionDockRoot = Path.Combine(LocalApplicationData, "SessionDock");
        SessionDockSettingsPath = Path.Combine(
            SessionDockRoot,
            "handlescope.json");
        LegacySessionDockSettingsPath = Path.Combine(
            LocalApplicationData,
            "RobloxOne",
            "handlescope.json");
        MaintenanceRoot = Path.Combine(
            LocalApplicationData,
            "HandleScope.Setup.Maintenance");
    }

    internal string LocalApplicationData { get; }
    internal string ProgramsRoot { get; }
    internal string ProductRoot { get; }
    internal string InstallRoot { get; }
    internal string StagingRoot { get; }
    internal string BackupRoot { get; }
    internal string TransactionPath { get; }
    internal string RuntimeRoot { get; }
    internal string ConnectionPath { get; }
    internal string LogPath { get; }
    internal string SessionDockRoot { get; }
    internal string SessionDockSettingsPath { get; }
    internal string LegacySessionDockSettingsPath { get; }
    internal string MaintenanceRoot { get; }
    internal string InstalledApiPath =>
        Path.Combine(InstallRoot, "HandleScope.Api.exe");
    internal string InstalledSetupPath =>
        Path.Combine(InstallRoot, "HandleScope.Setup.exe");
    internal string InstalledRuntimeManifestPath =>
        Path.Combine(InstallRoot, "HandleScope.runtime.json");

    internal void ValidateRoot()
    {
        if (!Directory.Exists(LocalApplicationData))
        {
            throw new SetupSafetyException(
                "Windows Local Application Data directory does not exist.");
        }
        AssertSafeExistingAncestors(LocalApplicationData);
    }

    internal void CreateDirectory(string path)
    {
        var safePath = AssertContained(path);
        var relative = Path.GetRelativePath(LocalApplicationData, safePath);
        var current = LocalApplicationData;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }
            AssertRegularDirectory(current);
        }
    }

    internal string AssertContained(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Normalize(path);
        var prefix = LocalApplicationData + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupSafetyException(
                $"Setup refused a path outside Local Application Data: {fullPath}");
        }
        AssertSafeExistingAncestors(fullPath);
        return fullPath;
    }

    internal void DeleteFileIfPresent(string path)
    {
        var safePath = AssertContained(path);
        if (!File.Exists(safePath))
        {
            return;
        }
        var info = new FileInfo(safePath);
        AssertRegularFile(info);
        File.Delete(safePath);
    }

    internal void DeleteTreeIfPresent(string path)
    {
        var safePath = AssertContained(path);
        if (!Directory.Exists(safePath))
        {
            return;
        }
        AssertTreeSafe(safePath);
        Directory.Delete(safePath, recursive: true);
    }

    internal void AssertTreeSafe(string path)
    {
        var safePath = AssertContained(path);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(safePath));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            AssertRegularDirectory(directory.FullName);
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SetupSafetyException(
                        $"A local setup tree contains a reparse point: {item.FullName}");
                }
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (item is not FileInfo file)
                {
                    throw new SetupSafetyException(
                        "A local setup tree contains an unsupported item.");
                }
                else
                {
                    AssertRegularFile(file);
                }
            }
        }
    }

    internal void MoveDirectory(string source, string destination)
    {
        var safeSource = AssertContained(source);
        var safeDestination = AssertContained(destination);
        AssertTreeSafe(safeSource);
        if (Directory.Exists(safeDestination) || File.Exists(safeDestination))
        {
            throw new SetupSafetyException(
                "A setup transaction destination already exists.");
        }
        Directory.Move(safeSource, safeDestination);
        AssertTreeSafe(safeDestination);
    }

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    private void AssertSafeExistingAncestors(string path)
    {
        var fullPath = Normalize(path);
        var rootPrefix = LocalApplicationData + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(
                LocalApplicationData,
                StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var current = LocalApplicationData;
        AssertRegularDirectory(current);
        if (fullPath.Equals(LocalApplicationData, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var relative = Path.GetRelativePath(LocalApplicationData, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                AssertRegularDirectory(current);
            }
            else if (File.Exists(current))
            {
                AssertRegularFile(new FileInfo(current));
            }
            else
            {
                break;
            }
        }
    }

    private static void AssertRegularDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupSafetyException(
                $"A local setup directory is missing or is a reparse point: {path}");
        }
    }

    private static void AssertRegularFile(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupSafetyException(
                $"A local setup file is missing or is a reparse point: {file.FullName}");
        }
    }
}
