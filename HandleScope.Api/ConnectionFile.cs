using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HandleScope.Api;

public sealed record ApiConnection(
    string ApiVersion,
    string BaseUrl,
    string Token,
    int ProcessId,
    DateTimeOffset StartedAtUtc);

public static class ConnectionFile
{
    public const int DefaultPort = 0;
    private const long MaximumLogBytes = 256 * 1024;
    private static readonly object LogSync = new();
    private static readonly List<string> CurrentProcessLog = [];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DefaultPath =>
        Path.Combine(DefaultDirectory, "connection.json");

    public static string DefaultLogPath =>
        Path.Combine(DefaultDirectory, "api.log");

    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandleScope");

    public static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static void Write(string path, ApiConnection connection)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The connection file path has no parent directory.");
        PrepareDirectory(directory);

        var json = JsonSerializer.Serialize(connection, SerializerOptions);
        WriteAtomic(path, json);
    }

    public static void DeleteIfOwned(string path, int processId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var connection = JsonSerializer.Deserialize<ApiConnection>(
                File.ReadAllText(path),
                SerializerOptions);
            if (connection?.ProcessId == processId)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale connection file is harmless; clients still verify /v1/health.
        }
    }

    public static void AppendLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(DefaultLogPath)!;
            PrepareDirectory(directory);
            lock (LogSync)
            {
                CurrentProcessLog.Add(
                    $"[{DateTimeOffset.UtcNow:O}] {message}");
                while (CurrentProcessLog.Count > 1 &&
                       Encoding.UTF8.GetByteCount(
                           string.Join(Environment.NewLine, CurrentProcessLog)) >
                       MaximumLogBytes)
                {
                    CurrentProcessLog.RemoveAt(0);
                }

                WriteAtomic(
                    DefaultLogPath,
                    string.Join(Environment.NewLine, CurrentProcessLog) +
                    Environment.NewLine);
            }
        }
        catch
        {
            // There is nowhere else to report a failure in the headless process.
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temporaryPath = path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) +
            ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ProtectFileIfDefault(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort: the random temporary file contains only the
                // same protected local data as its destination.
            }
        }
    }

    private static void PrepareDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(DefaultDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The HandleScope runtime directory cannot be a reparse point.");
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static void ProtectFileIfDefault(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(
                directory,
                Path.GetFullPath(DefaultDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
