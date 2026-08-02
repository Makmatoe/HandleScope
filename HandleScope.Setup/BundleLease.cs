using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace HandleScope.Setup;

internal sealed partial class BundleLease : IDisposable
{
    private const int MaximumFiles = 128;
    private const long MaximumTotalBytes = 512L * 1024 * 1024;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int ErrorHandleEof = 38;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FindStreamInfoStandard = 0;

    private static readonly IReadOnlySet<string> InstalledApiFiles =
        new HashSet<string>(
            [
                "API.md",
                "Enable-SessionDockIntegration.ps1",
                "HandleScope.Api.exe",
                "HandleScope.runtime.json",
                "HandleScope.ScriptCommon.ps1",
                "HandleScope.Setup.exe",
                "Install-HandleScopeApi.ps1",
                "Invoke-HandleScopeClose.ps1",
                "Start-HandleScopeApi.ps1",
                "Stop-HandleScopeApi.ps1",
                "Uninstall-HandleScopeApi.ps1"
            ],
            StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ExpectedBundleDirectories =
        new HashSet<string>(
            [string.Empty, "api", "desktop", "docs"],
            StringComparer.Ordinal);

    private readonly Dictionary<string, LockedFile> _files;
    private readonly HashSet<string> _directories;
    private readonly List<SafeFileHandle> _directoryHandles;
    private bool _disposed;

    private BundleLease(
        string bundleRoot,
        string apiRoot,
        Dictionary<string, LockedFile> files,
        HashSet<string> directories,
        List<SafeFileHandle> directoryHandles,
        RuntimeIdentity runtime)
    {
        BundleRoot = bundleRoot;
        ApiRoot = apiRoot;
        _files = files;
        _directories = directories;
        _directoryHandles = directoryHandles;
        Runtime = runtime;
    }

    internal string BundleRoot { get; }

    internal string ApiRoot { get; }

    internal RuntimeIdentity Runtime { get; }

    internal static BundleLease Acquire(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!string.Equals(
                Path.GetFileName(fullExecutablePath),
                "HandleScope.Setup.exe",
                StringComparison.Ordinal))
        {
            throw new SetupSafetyException(
                "The native setup executable has an unexpected file name.");
        }

        var apiRoot = Path.GetDirectoryName(fullExecutablePath)
            ?? throw new SetupSafetyException(
                "The native setup executable has no API directory.");
        if (!string.Equals(
                Path.GetFileName(apiRoot),
                "api",
                StringComparison.Ordinal))
        {
            throw new SetupSafetyException(
                "HandleScope.Setup.exe must run from the release API directory.");
        }

        var bundleRoot = Directory.GetParent(apiRoot)?.FullName
            ?? throw new SetupSafetyException(
                "The release API directory has no bundle root.");
        AssertAncestorsNotReparsePoints(bundleRoot);

        var directoryHandles = new List<SafeFileHandle>();
        var files = new Dictionary<string, LockedFile>(StringComparer.Ordinal);
        try
        {
            var inventory = EnumerateTree(bundleRoot);
            foreach (var directory in inventory.Directories)
            {
                directoryHandles.Add(OpenDirectory(directory));
            }

            var manifestPath = Path.Combine(bundleRoot, "CONTENTS.sha256");
            if (!inventory.Files.Contains(manifestPath))
            {
                throw new SetupSafetyException(
                    "The release contents manifest is missing.");
            }

            foreach (var path in inventory.Files)
            {
                var relativePath = GetRelativePath(bundleRoot, path);
                var stream = OpenLockedFile(path);
                try
                {
                    var identity = GetFileIdentity(stream.SafeFileHandle);
                    if (identity.LinkCount != 1)
                    {
                        throw new SetupSafetyException(
                            $"A release file is hard-linked: {relativePath}");
                    }
                    AssertExpectedStreams(
                        path,
                        relativePath,
                        allowSourceMetadata: true);
                    files.Add(
                        relativePath,
                        new LockedFile(
                            relativePath,
                            path,
                            stream,
                            identity,
                            stream.Length));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            var manifest = ReadManifest(files["CONTENTS.sha256"]);
            var actual = files.Keys
                .Where(path => path != "CONTENTS.sha256")
                .ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(manifest.Keys))
            {
                throw new SetupSafetyException(
                    "The release tree does not exactly match CONTENTS.sha256.");
            }

            var apiFiles = actual
                .Where(path => path.StartsWith("api/", StringComparison.Ordinal))
                .Select(path => path[4..])
                .ToHashSet(StringComparer.Ordinal);
            if (!apiFiles.SetEquals(InstalledApiFiles))
            {
                throw new SetupSafetyException(
                    "The release API directory does not match the compiled allowlist.");
            }

            foreach (var entry in manifest)
            {
                var file = files[entry.Key];
                var actualHash = Hash(file.Stream);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualHash),
                        Convert.FromHexString(entry.Value)))
                {
                    throw new SetupSafetyException(
                        $"Release integrity verification failed for {entry.Key}.");
                }
                file.Hash = actualHash;
            }

            var runtime = RuntimeIdentity.Read(
                files["api/HandleScope.runtime.json"].Stream);
            var directories = inventory.Directories
                .Select(path => GetRelativeDirectoryPath(bundleRoot, path))
                .ToHashSet(StringComparer.Ordinal);
            if (!directories.SetEquals(ExpectedBundleDirectories))
            {
                throw new SetupSafetyException(
                    "The release directory inventory is incomplete or unexpected.");
            }
            AssertExecutableVersions(files, runtime);
            var lease = new BundleLease(
                bundleRoot,
                apiRoot,
                files,
                directories,
                directoryHandles,
                runtime);
            lease.Revalidate();
            return lease;
        }
        catch
        {
            foreach (var file in files.Values)
            {
                file.Stream.Dispose();
            }
            foreach (var handle in directoryHandles)
            {
                handle.Dispose();
            }
            throw;
        }
    }

    internal void Revalidate()
    {
        ThrowIfDisposed();
        var inventory = EnumerateTree(BundleRoot);
        var actualFiles = inventory.Files
            .Select(path => GetRelativePath(BundleRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        var actualDirectories = inventory.Directories
            .Select(path => GetRelativeDirectoryPath(BundleRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(_files.Keys) ||
            !actualDirectories.SetEquals(_directories))
        {
            throw new SetupSafetyException(
                "The verified release tree changed before setup completed.");
        }

        foreach (var file in _files.Values)
        {
            var identity = GetFileIdentity(file.Stream.SafeFileHandle);
            if (identity != file.Identity || file.Stream.Length != file.Length)
            {
                throw new SetupSafetyException(
                    $"A verified release file changed identity: {file.RelativePath}");
            }
            AssertExpectedStreams(
                file.FullPath,
                file.RelativePath,
                allowSourceMetadata: true);
        }
    }

    internal void CopyApiFiles(string destination)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var fullDestination = Path.GetFullPath(destination);
        if (!Directory.Exists(fullDestination) ||
            Directory.EnumerateFileSystemEntries(fullDestination).Any())
        {
            throw new SetupSafetyException(
                "The installation staging directory is not empty.");
        }
        AssertAncestorsNotReparsePoints(fullDestination);

        foreach (var fileName in InstalledApiFiles.Order(StringComparer.Ordinal))
        {
            var source = _files[$"api/{fileName}"];
            var destinationPath = Path.Combine(fullDestination, fileName);
            source.Stream.Position = 0;
            using (var output = new FileStream(
                       destinationPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.WriteThrough))
            {
                source.Stream.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            using var copied = OpenLockedFile(destinationPath);
            var copiedIdentity = GetFileIdentity(copied.SafeFileHandle);
            if (copiedIdentity.LinkCount != 1 ||
                copied.Length != source.Length ||
                !string.Equals(Hash(copied), source.Hash, StringComparison.Ordinal))
            {
                throw new SetupSafetyException(
                    $"Staged-file verification failed for {fileName}.");
            }
            RemoveAlternateStream(destinationPath, "Zone.Identifier");
            RemoveAlternateStream(destinationPath, "mshield");
            AssertExpectedStreams(
                destinationPath,
                fileName,
                allowSourceMetadata: false);
        }

        var stagedFiles = Directory.EnumerateFiles(fullDestination)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!stagedFiles.SetEquals(InstalledApiFiles) ||
            Directory.EnumerateDirectories(fullDestination).Any())
        {
            throw new SetupSafetyException(
                "The staged installation does not match the compiled allowlist.");
        }
    }

    internal static IReadOnlySet<string> GetInstalledFileNames() =>
        InstalledApiFiles;

    internal void AssertApiSnapshotMatchesSource(ApiTreeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Runtime != Runtime ||
            snapshot.Files.Count != InstalledApiFiles.Count)
        {
            throw new SetupSafetyException(
                "The staged API identity does not match the verified source runtime.");
        }
        foreach (var fileName in InstalledApiFiles)
        {
            var source = _files[$"api/{fileName}"];
            if (!snapshot.Files.TryGetValue(fileName, out var staged) ||
                staged.Length != source.Length ||
                !string.Equals(staged.Sha256, source.Hash, StringComparison.Ordinal))
            {
                throw new SetupSafetyException(
                    $"The staged API bytes do not match the verified source: {fileName}");
            }
        }
    }

    internal static InstalledApiLease AcquireInstalledApiLease(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        AssertAncestorsNotReparsePoints(fullRoot);
        var directories = Directory.EnumerateDirectories(fullRoot).ToArray();
        var paths = Directory.EnumerateFiles(fullRoot).ToArray();
        var names = paths.Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (directories.Length != 0 || !names.SetEquals(InstalledApiFiles))
        {
            throw new SetupSafetyException(
                "An installed API tree has an incomplete or unexpected inventory.");
        }

        var handles = new List<FileStream>();
        try
        {
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                var stream = OpenLockedFile(path);
                try
                {
                    var identity = GetFileIdentity(stream.SafeFileHandle);
                    if (identity.LinkCount != 1)
                    {
                        throw new SetupSafetyException(
                            $"An installed API file is hard-linked: {Path.GetFileName(path)}");
                    }
                    AssertExpectedStreams(
                        path,
                        Path.GetFileName(path),
                        allowSourceMetadata: false);
                    handles.Add(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            var runtimeStream = handles.Single(stream =>
                string.Equals(
                    Path.GetFileName(stream.Name),
                    "HandleScope.runtime.json",
                    StringComparison.Ordinal));
            var runtime = RuntimeIdentity.Read(runtimeStream);
            var mapped = handles.ToDictionary(
                stream => $"api/{Path.GetFileName(stream.Name)}",
                stream => new LockedFile(
                    $"api/{Path.GetFileName(stream.Name)}",
                    stream.Name,
                    stream,
                    GetFileIdentity(stream.SafeFileHandle),
                    stream.Length),
                StringComparer.Ordinal);
            AssertExecutableVersions(mapped, runtime);
            var snapshots = handles.ToDictionary(
                stream => Path.GetFileName(stream.Name),
                stream =>
                {
                    var identity = GetFileIdentity(stream.SafeFileHandle);
                    return new ApiFileSnapshot(
                        identity.VolumeSerialNumber,
                        identity.FileIndexHigh,
                        identity.FileIndexLow,
                        identity.LinkCount,
                        stream.Length,
                        Hash(stream));
                },
                StringComparer.Ordinal);
            return new InstalledApiLease(
                fullRoot,
                handles,
                new ApiTreeSnapshot(runtime, snapshots));
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var file in _files.Values)
        {
            file.Stream.Dispose();
        }
        foreach (var handle in _directoryHandles)
        {
            handle.Dispose();
        }
    }

    private static TreeInventory EnumerateTree(string bundleRoot)
    {
        try
        {
            var root = new DirectoryInfo(bundleRoot);
            AssertNotReparsePoint(root);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exactFiles = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                root.FullName
            };
            var exactDirectories = new HashSet<string>(StringComparer.Ordinal)
            {
                root.FullName
            };
            long totalLength = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    AssertNotReparsePoint(item);
                    if (item is DirectoryInfo child)
                    {
                        if (!directories.Add(child.FullName) ||
                            !exactDirectories.Add(child.FullName))
                        {
                            throw new SetupSafetyException(
                                "The release contains a case-colliding directory.");
                        }
                        pending.Push(child);
                    }
                    else if (item is FileInfo file)
                    {
                        if (!files.Add(file.FullName) || !exactFiles.Add(file.FullName))
                        {
                            throw new SetupSafetyException(
                                "The release contains a case-colliding file.");
                        }
                        totalLength = checked(totalLength + file.Length);
                        if (files.Count > MaximumFiles || totalLength > MaximumTotalBytes)
                        {
                            throw new SetupSafetyException(
                                "The release inventory exceeds its compiled bounds.");
                        }
                    }
                    else
                    {
                        throw new SetupSafetyException(
                            "The release contains an unsupported file-system item.");
                    }
                }
            }
            return new TreeInventory(files, directories);
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or OverflowException)
        {
            throw new SetupSafetyException(
                "The release inventory could not be inspected safely.",
                exception);
        }
    }

    private static Dictionary<string, string> ReadManifest(LockedFile manifest)
    {
        manifest.Stream.Position = 0;
        using var reader = new StreamReader(
            manifest.Stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = ManifestLine().Match(line);
            if (!match.Success)
            {
                throw new SetupSafetyException(
                    "CONTENTS.sha256 contains a malformed entry.");
            }
            var path = match.Groups["path"].Value;
            var segments = path.Split('/');
            if (Path.IsPathRooted(path) ||
                path.Contains('\\', StringComparison.Ordinal) ||
                path.Contains(':', StringComparison.Ordinal) ||
                segments.Any(segment =>
                    segment.Length == 0 || segment is "." or "..") ||
                path == "CONTENTS.sha256" ||
                !caseInsensitivePaths.Add(path) ||
                !entries.TryAdd(path, match.Groups["hash"].Value))
            {
                throw new SetupSafetyException(
                    "CONTENTS.sha256 contains an unsafe or duplicate path.");
            }
        }
        if (entries.Count is <= 0 or >= MaximumFiles)
        {
            throw new SetupSafetyException(
                "CONTENTS.sha256 contains an invalid number of entries.");
        }
        return entries;
    }

    private static FileStream OpenLockedFile(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new SetupSafetyException(
                $"A release file could not be locked safely: {Path.GetFileName(path)}",
                exception);
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SetupSafetyException(
                "A release directory could not be locked safely.",
                new Win32Exception(error));
        }
        return handle;
    }

    private static void AssertAncestorsNotReparsePoints(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            AssertNotReparsePoint(current);
            current = current.Parent;
        }
    }

    private static void AssertNotReparsePoint(FileSystemInfo item)
    {
        item.Refresh();
        if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupSafetyException(
                $"A setup path is missing or is a reparse point: {item.FullName}");
        }
    }

    private static FileIdentity GetFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new SetupSafetyException(
                "Windows could not inspect a release file identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            information.NumberOfLinks);
    }

    private static void AssertExpectedStreams(
        string path,
        string description,
        bool allowSourceMetadata)
    {
        var streams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataStreamCount = 0;
        long metadataStreamBytes = 0;
        var findHandle = FindFirstStreamW(
            path,
            FindStreamInfoStandard,
            out var data,
            0);
        if (findHandle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                throw new SetupSafetyException(
                    $"A release file disappeared: {description}");
            }
            throw new SetupSafetyException(
                $"Alternate streams could not be inspected for {description}.",
                new Win32Exception(error));
        }

        try
        {
            while (true)
            {
                if (!streams.Add(data.StreamName))
                {
                    throw new SetupSafetyException(
                        $"A release file has an unexpected alternate stream {data.StreamName}: {description}");
                }
                if (data.StreamName == "::$DATA")
                {
                    // The locked unnamed stream is the only stream hashed or copied.
                }
                else if (data.StreamName == ":Zone.Identifier:$DATA")
                {
                    if (!allowSourceMetadata)
                    {
                        throw new SetupSafetyException(
                            $"A staged or installed file still has download metadata: {description}");
                    }
                }
                else if (!allowSourceMetadata ||
                    !NamedDataStream().IsMatch(data.StreamName) ||
                    data.StreamSize is < 0 or > 64 * 1024 ||
                    ++metadataStreamCount > 8 ||
                    (metadataStreamBytes = checked(
                        metadataStreamBytes + data.StreamSize)) > 128 * 1024)
                {
                    throw new SetupSafetyException(
                        $"A release file has excessive or malformed source-only metadata {data.StreamName}: {description}");
                }
                if (FindNextStreamW(findHandle, out data))
                {
                    continue;
                }
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorHandleEof)
                {
                    throw new SetupSafetyException(
                        $"Alternate streams changed while inspecting {description}.",
                        new Win32Exception(error));
                }
                break;
            }
        }
        finally
        {
            FindClose(findHandle);
        }
        if (!streams.Contains("::$DATA"))
        {
            throw new SetupSafetyException(
                $"A release file has no unnamed data stream: {description}");
        }
        if (streams.Contains(":Zone.Identifier:$DATA"))
        {
            ValidateZoneIdentifier(path, description);
        }
    }

    private static void ValidateZoneIdentifier(string path, string description)
    {
        try
        {
            using var stream = new FileStream(
                path + ":Zone.Identifier",
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 4096)
            {
                throw new SetupSafetyException(
                    $"Download metadata has an invalid size: {description}");
            }
            using var reader = new StreamReader(
                stream,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var text = reader.ReadToEnd();
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                throw new SetupSafetyException(
                    $"Download metadata contains a null character: {description}");
            }
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n');
            if (lines.Length < 2 || lines[0] != "[ZoneTransfer]")
            {
                throw new SetupSafetyException(
                    $"Download metadata has an invalid section: {description}");
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1).Where(line => line.Length != 0))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !ZoneKey().IsMatch(line[..separator]) ||
                    !keys.Add(line[..separator]) ||
                    line[(separator + 1)..].Any(character =>
                        char.IsControl(character) && character != '\t'))
                {
                    throw new SetupSafetyException(
                        $"Download metadata contains an invalid entry: {description}");
                }
            }
            if (!keys.Contains("ZoneId"))
            {
                throw new SetupSafetyException(
                    $"Download metadata does not identify a Windows zone: {description}");
            }
            var zoneLine = lines.Single(line =>
                line.StartsWith("ZoneId=", StringComparison.Ordinal));
            if (!int.TryParse(
                    zoneLine["ZoneId=".Length..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var zone) ||
                zone is < 0 or > 4)
            {
                throw new SetupSafetyException(
                    $"Download metadata has an invalid Windows zone: {description}");
            }
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Text.DecoderFallbackException)
        {
            throw new SetupSafetyException(
                $"Download metadata could not be validated: {description}",
                exception);
        }
    }

    private static void AssertExecutableVersions(
        IReadOnlyDictionary<string, LockedFile> files,
        RuntimeIdentity runtime)
    {
        var expectedAssemblyVersion = typeof(BundleLease).Assembly
            .GetName().Version
            ?? throw new SetupSafetyException(
                "The native setup assembly has no version identity.");
        var expectedVersion = new Version(
            expectedAssemblyVersion.Major,
            expectedAssemblyVersion.Minor,
            Math.Max(0, expectedAssemblyVersion.Build));
        if (runtime.Version != expectedVersion)
        {
            throw new SetupSafetyException(
                "The setup executable and runtime manifest versions disagree.");
        }
        foreach (var path in new[]
                 {
                     "api/HandleScope.Api.exe",
                     "api/HandleScope.Setup.exe"
                 })
        {
            var text = FileVersionInfo.GetVersionInfo(files[path].FullPath).FileVersion;
            if (!SetupInstaller.TryNormalizeVersion(text, out var version) ||
                version != runtime.Version)
            {
                throw new SetupSafetyException(
                    $"A release executable version disagrees with the runtime manifest: {path}");
            }
        }
    }

    private static void RemoveAlternateStream(string path, string streamName)
    {
        if (DeleteFileW(path + ":" + streamName))
        {
            return;
        }
        var error = Marshal.GetLastWin32Error();
        if (error is not (ErrorFileNotFound or ErrorPathNotFound))
        {
            throw new SetupSafetyException(
                "Windows could not remove download metadata from a verified staged file.",
                new Win32Exception(error));
        }
    }

    private static string Hash(Stream stream)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }

    private static string GetRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string GetRelativeDirectoryPath(string root, string path)
    {
        var relative = GetRelativePath(root, path);
        return relative == "." ? string.Empty : relative;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [GeneratedRegex("^(?<hash>[0-9a-f]{64})  (?<path>[^\\\\\\r\\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestLine();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneKey();

    [GeneratedRegex("^:[A-Za-z0-9][A-Za-z0-9._-]{0,63}:\\$DATA$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedDataStream();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName,
        int infoLevel,
        out Win32FindStreamData data,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        IntPtr findStream,
        out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string fileName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    private sealed class LockedFile(
        string relativePath,
        string fullPath,
        FileStream stream,
        FileIdentity identity,
        long length)
    {
        internal string RelativePath { get; } = relativePath;
        internal string FullPath { get; } = fullPath;
        internal FileStream Stream { get; } = stream;
        internal FileIdentity Identity { get; } = identity;
        internal long Length { get; } = length;
        internal string Hash { get; set; } = string.Empty;
    }

    private sealed record TreeInventory(
        HashSet<string> Files,
        HashSet<string> Directories);

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        uint LinkCount);

    internal sealed class InstalledApiLease : IDisposable
    {
        private readonly IReadOnlyList<FileStream> _handles;

        internal InstalledApiLease(
            string root,
            IReadOnlyList<FileStream> handles,
            ApiTreeSnapshot snapshot)
        {
            Root = root;
            _handles = handles;
            Snapshot = snapshot;
        }

        internal string Root { get; }

        internal RuntimeIdentity Runtime => Snapshot.Runtime;

        internal ApiTreeSnapshot Snapshot { get; }

        internal void AssertSameFiles(ApiTreeSnapshot expected)
        {
            ArgumentNullException.ThrowIfNull(expected);
            if (Snapshot.Runtime != expected.Runtime ||
                Snapshot.Files.Count != expected.Files.Count ||
                expected.Files.Any(entry =>
                    !Snapshot.Files.TryGetValue(entry.Key, out var actual) ||
                    actual != entry.Value))
            {
                throw new SetupSafetyException(
                    "The promoted API tree changed identity or bytes during the atomic rename gap.");
            }
        }

        public void Dispose()
        {
            foreach (var handle in _handles)
            {
                handle.Dispose();
            }
        }
    }

    internal sealed record ApiTreeSnapshot(
        RuntimeIdentity Runtime,
        IReadOnlyDictionary<string, ApiFileSnapshot> Files)
    {
        internal string CanonicalDigest
        {
            get
            {
                var value = new System.Text.StringBuilder();
                value.Append("handlescope-installed-api/v1\n");
                value.Append(Runtime.VersionText).Append('\n');
                value.Append(Runtime.SourceRevision).Append('\n');
                foreach (var entry in Files.OrderBy(
                             entry => entry.Key,
                             StringComparer.Ordinal))
                {
                    value.Append(entry.Key).Append('\n');
                    value.Append(entry.Value.VolumeSerialNumber)
                        .Append(':')
                        .Append(entry.Value.FileIndexHigh)
                        .Append(':')
                        .Append(entry.Value.FileIndexLow)
                        .Append(':')
                        .Append(entry.Value.LinkCount)
                        .Append(':')
                        .Append(entry.Value.Length)
                        .Append('\n');
                    value.Append(entry.Value.Sha256).Append('\n');
                }
                return Convert.ToHexString(SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(value.ToString())))
                    .ToLowerInvariant();
            }
        }
    }

    internal readonly record struct ApiFileSnapshot(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        uint LinkCount,
        long Length,
        string Sha256);
}

internal sealed record RuntimeIdentity(
    Version Version,
    string VersionText,
    string SourceRevision)
{
    internal static RuntimeIdentity Read(Stream stream)
    {
        stream.Position = 0;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                stream,
                new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            var required = new HashSet<string>(
                [
                    "schemaVersion", "product", "repository", "version", "tag",
                    "sourceRevision", "sourceTimestamp", "runtime",
                    "discoveryApiVersion", "supportedApiVersions",
                    "preferredApiVersion", "policies", "capabilities"
                ],
                StringComparer.Ordinal);
            if (names.Length != required.Count ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(required) ||
                root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("product").GetString() != "HandleScope.Api" ||
                root.GetProperty("repository").GetString() != "Makmatoe/HandleScope" ||
                root.GetProperty("runtime").GetString() != "win-x64" ||
                root.GetProperty("discoveryApiVersion").GetString() != "v1" ||
                root.GetProperty("preferredApiVersion").GetString() != "v2" ||
                !ReadExactStringArray(
                    root.GetProperty("supportedApiVersions"),
                    ["v1", "v2"]) ||
                !ReadExactStringArray(
                    root.GetProperty("policies"),
                    ["roblox-singleton-event-v1"]) ||
                !ReadExactStringArray(
                    root.GetProperty("capabilities"),
                    [
                        "handlescope.http.v1",
                        "handlescope.http.v2",
                        "handlescope.plan.single-use.v1",
                        "handlescope.policy.roblox-singleton-event.v1",
                        "handlescope.setup.native.v1"
                    ]))
            {
                throw new SetupSafetyException(
                    "The HandleScope runtime manifest identity is invalid.");
            }

            var versionText = root.GetProperty("version").GetString();
            if (versionText is null ||
                !Version.TryParse(versionText, out var version) ||
                version.Revision >= 0 ||
                version.Build < 0 ||
                version.ToString(3) != versionText ||
                root.GetProperty("tag").GetString() != $"v{versionText}")
            {
                throw new SetupSafetyException(
                    "The HandleScope runtime manifest version is invalid.");
            }
            var revision = root.GetProperty("sourceRevision").GetString();
            if (revision is null || revision.Length != 40 ||
                revision.Any(character =>
                    character is not (>= '0' and <= '9') and
                        not (>= 'a' and <= 'f')))
            {
                throw new SetupSafetyException(
                    "The HandleScope runtime manifest source revision is invalid.");
            }
            var timestampText = root.GetProperty("sourceTimestamp").GetString();
            if (timestampText is null ||
                !DateTimeOffset.TryParseExact(
                    timestampText,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var timestamp) ||
                timestamp.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture) != timestampText)
            {
                throw new SetupSafetyException(
                    "The HandleScope runtime manifest source timestamp is invalid.");
            }
            return new RuntimeIdentity(version, versionText, revision);
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or InvalidOperationException or
                FormatException or OverflowException)
        {
            throw new SetupSafetyException(
                "The HandleScope runtime manifest is malformed.",
                exception);
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static bool ReadExactStringArray(
        System.Text.Json.JsonElement element,
        IReadOnlyList<string> expected)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }
        var values = element.EnumerateArray().ToArray();
        return values.Length == expected.Count &&
            values.Select(value => value.ValueKind ==
                    System.Text.Json.JsonValueKind.String
                    ? value.GetString()
                    : null)
                .SequenceEqual(expected, StringComparer.Ordinal);
    }
}
