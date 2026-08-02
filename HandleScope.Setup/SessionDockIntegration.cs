using System.Text;
using System.Text.Json;

namespace HandleScope.Setup;

internal interface ISessionDockIntegration
{
    void Enable(bool force);
}

internal sealed class SessionDockIntegration : ISessionDockIntegration
{
    private const int MaximumSettingsBytes = 4096;
    private const string MinimalSettings = "{\n  \"enabled\": true\n}\n";
    private readonly SetupPaths _paths;

    internal SessionDockIntegration(SetupPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public void Enable(bool force)
    {
        var canonicalExists = File.Exists(_paths.SessionDockSettingsPath);
        if (canonicalExists && IsMinimalEnabled(_paths.SessionDockSettingsPath))
        {
            Console.WriteLine(
                $"SessionDock integration is already enabled at {_paths.SessionDockSettingsPath}");
            return;
        }
        if (canonicalExists && !force)
        {
            throw new SetupSafetyException(
                "SessionDock already has a non-minimal handlescope.json. Use --force only if replacing it is intended.");
        }

        var migrateLegacy = false;
        if (!canonicalExists && File.Exists(_paths.LegacySessionDockSettingsPath))
        {
            migrateLegacy = IsMinimalEnabled(
                _paths.LegacySessionDockSettingsPath);
            if (!migrateLegacy && !force)
            {
                throw new SetupSafetyException(
                    "The legacy SessionDock HandleScope setting is not the minimal opt-in. It was preserved.");
            }
        }

        _paths.CreateDirectory(_paths.SessionDockRoot);
        var temporary = Path.Combine(
            _paths.SessionDockRoot,
            $"handlescope.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(
            _paths.SessionDockRoot,
            $"handlescope.{Guid.NewGuid():N}.bak");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(MinimalSettings);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (canonicalExists)
            {
                if (!File.Exists(_paths.SessionDockSettingsPath))
                {
                    throw new SetupSafetyException(
                        "The SessionDock setting changed before replacement.");
                }
                AssertSmallRegularFile(_paths.SessionDockSettingsPath);
                File.Replace(
                    temporary,
                    _paths.SessionDockSettingsPath,
                    backup,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, _paths.SessionDockSettingsPath);
            }
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            throw new SetupOperationException(
                "SessionDock integration could not be written atomically.",
                exception);
        }
        finally
        {
            _paths.DeleteFileIfPresent(temporary);
            _paths.DeleteFileIfPresent(backup);
        }

        if (!IsMinimalEnabled(_paths.SessionDockSettingsPath))
        {
            throw new SetupOperationException(
                "The SessionDock integration setting did not validate after writing.");
        }
        Console.WriteLine(
            $"SessionDock integration enabled at {_paths.SessionDockSettingsPath}");
        if (migrateLegacy)
        {
            Console.WriteLine(
                "The legacy minimal opt-in was copied without modifying legacy data.");
        }
    }

    internal bool IsMinimalEnabled(string path)
    {
        AssertSmallRegularFile(path);
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            var properties = document.RootElement.EnumerateObject().ToArray();
            return properties.Length == 1 &&
                properties[0].Name == "enabled" &&
                properties[0].Value.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private void AssertSmallRegularFile(string path)
    {
        _paths.AssertContained(path);
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists || file.Length is <= 0 or > MaximumSettingsBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupSafetyException(
                "A SessionDock HandleScope setting is not a small regular file.");
        }
    }
}
