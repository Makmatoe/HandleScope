using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HandleScope.Services;
using Microsoft.Win32.SafeHandles;

namespace HandleScope.Setup;

internal interface IMaintenanceLauncher
{
    bool RequiresMaintenanceCopy(string currentExecutablePath);

    void LaunchUninstall(string currentExecutablePath, bool keepDiagnostics);
}

internal sealed class MaintenanceLauncher : IMaintenanceLauncher
{
    internal const string ParentEnvironmentName =
        "HANDLESCOPE_SETUP_MAINTENANCE_PARENT";
    private readonly SetupPaths _paths;
    private readonly SetupIdentity _identity;

    internal MaintenanceLauncher(SetupPaths paths, SetupIdentity identity)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public bool RequiresMaintenanceCopy(string currentExecutablePath)
    {
        var current = SetupPaths.Normalize(currentExecutablePath);
        var prefix = _paths.InstallRoot + Path.DirectorySeparatorChar;
        return current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public void LaunchUninstall(
        string currentExecutablePath,
        bool keepDiagnostics)
    {
        var source = SetupPaths.Normalize(currentExecutablePath);
        if (!string.Equals(
                source,
                SetupPaths.Normalize(_paths.InstalledSetupPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupSafetyException(
                "Only the installed native setup may create an uninstall maintenance copy.");
        }
        _paths.AssertTreeSafe(_paths.InstallRoot);
        _paths.CreateDirectory(_paths.MaintenanceRoot);
        var operationRoot = Path.Combine(
            _paths.MaintenanceRoot,
            Guid.NewGuid().ToString("N"));
        _paths.CreateDirectory(operationRoot);
        var destination = Path.Combine(operationRoot, "HandleScope.Setup.exe");
        try
        {
            WithVerifiedCopyLocked(source, destination, _ =>
            {
                var parent = new ProcessIdentityService().GetIdentity(
                    Environment.ProcessId);
                if (parent.OwnerSid != _identity.Sid ||
                    parent.WindowsSessionId != _identity.SessionId ||
                    parent.IsElevated ||
                    !string.Equals(
                        SetupPaths.Normalize(parent.ImagePath),
                        source,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new SetupSafetyException(
                        "The uninstall maintenance parent identity is invalid.");
                }
                var startInfo = new ProcessStartInfo
                {
                    FileName = destination,
                    WorkingDirectory = operationRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("uninstall");
                if (keepDiagnostics)
                {
                    startInfo.ArgumentList.Add("--keep-diagnostics");
                }
                foreach (var key in startInfo.Environment.Keys
                             .Where(key => key.StartsWith(
                                 "HANDLESCOPE_SETUP_",
                                 StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    startInfo.Environment.Remove(key);
                }
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var readyName = $@"Local\HandleScope.Setup.Maintenance.{nonce}";
                using var ready = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    readyName,
                    out var createdNew);
                if (!createdNew)
                {
                    throw new SetupSafetyException(
                        "The uninstall maintenance readiness identity already exists.");
                }
                startInfo.Environment[ParentEnvironmentName] =
                    $"{parent.ProcessId}:{parent.CreationTimeUtcFileTime}:{nonce}";
                using var child = Process.Start(startInfo)
                    ?? throw new SetupOperationException(
                        "Windows could not start the uninstall maintenance copy.");
                if (!ready.WaitOne(TimeSpan.FromSeconds(20)))
                {
                    try
                    {
                        child.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // The failed child may already have exited.
                    }
                    throw new SetupOperationException(
                        "The uninstall maintenance copy did not authenticate its parent in time.");
                }
                Console.WriteLine(
                    "The verified native maintenance copy will finish uninstall after this process exits.");
            });
        }
        catch
        {
            _paths.DeleteTreeIfPresent(operationRoot);
            throw;
        }
    }

    internal static void WithVerifiedCopyLocked(
        string source,
        string destination,
        Action<string> verifiedAction)
    {
        ArgumentNullException.ThrowIfNull(verifiedAction);
        using var input = OpenLockedRegularFile(
            source,
            desiredAccess: 0x80000000,
            creationDisposition: 3,
            fileAccess: FileAccess.Read,
            writeThrough: false);
        using (var output = OpenLockedRegularFile(
                   destination,
                   desiredAccess: 0x40000000,
                   creationDisposition: 1,
                   fileAccess: FileAccess.Write,
                   writeThrough: true,
                   shareMode: 0))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        using var copied = OpenLockedRegularFile(
            destination,
            desiredAccess: 0x80000000,
            creationDisposition: 3,
            fileAccess: FileAccess.Read,
            writeThrough: false);
        input.Position = 0;
        var sourceHash = SHA256.HashData(input);
        var copiedHash = SHA256.HashData(copied);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash))
        {
            throw new SetupSafetyException(
                "The uninstall maintenance copy failed integrity verification.");
        }
        verifiedAction(destination);
    }

    private static FileStream OpenLockedRegularFile(
        string path,
        uint desiredAccess,
        uint creationDisposition,
        FileAccess fileAccess,
        bool writeThrough,
        uint shareMode = 0x00000001)
    {
        var flags = 0x00200000u | 0x00000080u;
        if (writeThrough)
        {
            flags |= 0x80000000u;
        }
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not lock {path} safely.");
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not inspect {path} safely.");
            }
            if ((information.FileAttributes &
                    (uint)FileAttributes.ReparsePoint) != 0 ||
                information.NumberOfLinks != 1)
            {
                throw new SetupSafetyException(
                    "A maintenance executable is a reparse point or hardlink.");
            }
            return new FileStream(
                handle,
                fileAccess,
                bufferSize: 128 * 1024,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void WaitForAuthenticatedParent(
        SetupPaths paths,
        SetupIdentity identity)
    {
        var value = Environment.GetEnvironmentVariable(ParentEnvironmentName);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        Environment.SetEnvironmentVariable(ParentEnvironmentName, null);
        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var processId) || processId <= 0 ||
            !long.TryParse(parts[1], out var creationTime) || creationTime <= 0 ||
            parts[2].Length != 32 ||
            parts[2].Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new SetupSafetyException(
                "The uninstall maintenance handshake is malformed.");
        }
        var currentPath = Environment.ProcessPath
            ?? throw new SetupSafetyException(
                "The maintenance executable path is unavailable.");
        var maintenancePrefix = paths.MaintenanceRoot +
            Path.DirectorySeparatorChar;
        if (!SetupPaths.Normalize(currentPath).StartsWith(
                maintenancePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupSafetyException(
                "The uninstall maintenance process is outside its fixed local root.");
        }

        HandleScope.Models.ProcessIdentity parent;
        try
        {
            parent = new ProcessIdentityService().GetIdentity(processId);
        }
        catch (Exception exception)
        {
            throw new SetupSafetyException(
                "The uninstall maintenance parent could not be authenticated.",
                exception);
        }
        if (parent.CreationTimeUtcFileTime != creationTime ||
            parent.OwnerSid != identity.Sid ||
            parent.WindowsSessionId != identity.SessionId ||
            parent.IsElevated ||
            !string.Equals(
                SetupPaths.Normalize(parent.ImagePath),
                SetupPaths.Normalize(paths.InstalledSetupPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupSafetyException(
                "The uninstall maintenance parent identity is invalid.");
        }
        EventWaitHandle? ready = null;
        try
        {
            ready = EventWaitHandle.OpenExisting(
                $@"Local\HandleScope.Setup.Maintenance.{parts[2]}");
            ready.Set();
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(30_000))
            {
                throw new SetupOperationException(
                    "The uninstall maintenance parent did not exit in time.");
            }
        }
        catch (ArgumentException)
        {
            // The exact authenticated parent already exited.
        }
        catch (WaitHandleCannotBeOpenedException exception)
        {
            throw new SetupSafetyException(
                "The uninstall maintenance readiness handshake is unavailable.",
                exception);
        }
        finally
        {
            ready?.Dispose();
        }
    }

    internal static void CleanupStaleCopies(
        SetupPaths paths,
        string currentExecutablePath)
    {
        if (!Directory.Exists(paths.MaintenanceRoot))
        {
            return;
        }
        paths.AssertTreeSafe(paths.MaintenanceRoot);
        var currentDirectory = Path.GetDirectoryName(
            SetupPaths.Normalize(currentExecutablePath));
        foreach (var directory in Directory.EnumerateDirectories(
                     paths.MaintenanceRoot))
        {
            if (string.Equals(
                    SetupPaths.Normalize(directory),
                    currentDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                paths.DeleteTreeIfPresent(directory);
            }
            catch (IOException)
            {
                // A different authenticated maintenance copy can still be active.
            }
            catch (UnauthorizedAccessException)
            {
                // Failures are retried on a later setup invocation.
            }
        }
    }

    internal static void TryDeleteCurrentMaintenanceCopy(
        SetupPaths paths,
        string currentExecutablePath)
    {
        var current = SetupPaths.Normalize(currentExecutablePath);
        var prefix = paths.MaintenanceRoot + Path.DirectorySeparatorChar;
        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            using var handle = CreateFile(
                current,
                0x00010000,
                0x00000001 | 0x00000002 | 0x00000004,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return;
            }
            var information = new FileDispositionInfoEx
            {
                Flags = 0x00000001 | 0x00000002 | 0x00000010
            };
            _ = SetFileInformationByHandle(
                handle,
                21,
                ref information,
                (uint)Marshal.SizeOf<FileDispositionInfoEx>());
        }
        catch
        {
            // A stale verified helper is retried by CleanupStaleCopies.
        }
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfoEx fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        internal uint Flags;
    }

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
}
