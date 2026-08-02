using System.Text;
using System.Text.Json;

namespace HandleScope.Setup;

internal sealed class InstallationTransaction
{
    private const string Staged = "staged";
    private const string Preparing = "preparing";
    private const string Promoted = "promoted";
    private readonly SetupPaths _paths;

    internal InstallationTransaction(SetupPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    internal void Recover()
    {
        var transaction = ReadTransaction();
        var stagingExists = Directory.Exists(_paths.StagingRoot);
        var backupExists = Directory.Exists(_paths.BackupRoot);
        var installExists = Directory.Exists(_paths.InstallRoot);
        if (transaction is null)
        {
            if (stagingExists || backupExists)
            {
                throw new SetupSafetyException(
                    "Untracked setup transaction files require manual inspection.");
            }
            return;
        }

        switch (transaction.Phase)
        {
            case Preparing:
                if (backupExists || installExists != transaction.HadExisting)
                {
                    throw new SetupSafetyException(
                        "The interrupted preparation transaction has an ambiguous state.");
                }
                _paths.DeleteTreeIfPresent(_paths.StagingRoot);
                break;
            case Staged:
                if (transaction.HadExisting && !installExists && backupExists)
                {
                    _paths.MoveDirectory(_paths.BackupRoot, _paths.InstallRoot);
                }
                else if (transaction.HadExisting && installExists && !backupExists)
                {
                    // The old install was not moved yet.
                }
                else if (transaction.HadExisting && installExists && backupExists &&
                    !stagingExists)
                {
                    using var promoted =
                        BundleLease.AcquireInstalledApiLease(_paths.InstallRoot);
                    AssertRecoveryDigest(promoted, transaction.TreeDigest);
                    _paths.DeleteTreeIfPresent(_paths.BackupRoot);
                }
                else if (!transaction.HadExisting && !installExists && !backupExists)
                {
                    // A first install stopped before promotion.
                }
                else if (!transaction.HadExisting && installExists &&
                    !backupExists && !stagingExists)
                {
                    using var promoted =
                        BundleLease.AcquireInstalledApiLease(_paths.InstallRoot);
                    AssertRecoveryDigest(promoted, transaction.TreeDigest);
                }
                else
                {
                    throw new SetupSafetyException(
                        "The interrupted staged transaction has an ambiguous state.");
                }
                _paths.DeleteTreeIfPresent(_paths.StagingRoot);
                break;
            case Promoted:
                if (!installExists)
                {
                    if (transaction.HadExisting && backupExists)
                    {
                        _paths.MoveDirectory(_paths.BackupRoot, _paths.InstallRoot);
                    }
                    else if (transaction.HadExisting || backupExists)
                    {
                        throw new SetupSafetyException(
                            "The promoted installation has an ambiguous recovery state.");
                    }
                }
                else
                {
                    using var promoted =
                        BundleLease.AcquireInstalledApiLease(_paths.InstallRoot);
                    AssertRecoveryDigest(promoted, transaction.TreeDigest);
                    _paths.DeleteTreeIfPresent(_paths.BackupRoot);
                }
                _paths.DeleteTreeIfPresent(_paths.StagingRoot);
                break;
            default:
                throw new SetupSafetyException(
                    "The setup transaction phase is invalid.");
        }
        _paths.DeleteFileIfPresent(_paths.TransactionPath);
    }

    internal void Replace(BundleLease bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        Recover();
        _paths.CreateDirectory(_paths.ProductRoot);
        var hadExistingInstall = Directory.Exists(_paths.InstallRoot);
        WriteTransaction(Preparing, hadExistingInstall, treeDigest: null);
        _paths.CreateDirectory(_paths.StagingRoot);
        try
        {
            bundle.Revalidate();
            bundle.CopyApiFiles(_paths.StagingRoot);
            BundleLease.ApiTreeSnapshot stagedSnapshot;
            using (var stagingLease =
                   BundleLease.AcquireInstalledApiLease(_paths.StagingRoot))
            {
                bundle.AssertApiSnapshotMatchesSource(stagingLease.Snapshot);
                stagedSnapshot = stagingLease.Snapshot;
                WriteTransaction(
                    Staged,
                    hadExistingInstall,
                    stagedSnapshot.CanonicalDigest);
            }

            // Windows does not permit a directory rename while any child file
            // handle is open, even when that handle shares delete access. Keep
            // this unavoidable close/rename/reacquire interval to only the two
            // directory renames below. The source remains locked throughout;
            // the promoted lease must match every recorded file ID, length, and
            // source-matched hash before any task, process, or setting action.
            if (hadExistingInstall)
            {
                _paths.MoveDirectory(_paths.InstallRoot, _paths.BackupRoot);
            }
            try
            {
                _paths.MoveDirectory(_paths.StagingRoot, _paths.InstallRoot);
                using (var installedLease =
                       BundleLease.AcquireInstalledApiLease(_paths.InstallRoot))
                {
                    installedLease.AssertSameFiles(stagedSnapshot);
                    bundle.AssertApiSnapshotMatchesSource(installedLease.Snapshot);
                }
                bundle.Revalidate();
                WriteTransaction(
                    Promoted,
                    hadExistingInstall,
                    stagedSnapshot.CanonicalDigest);
            }
            catch
            {
                _paths.DeleteTreeIfPresent(_paths.InstallRoot);
                if (hadExistingInstall && Directory.Exists(_paths.BackupRoot))
                {
                    _paths.MoveDirectory(_paths.BackupRoot, _paths.InstallRoot);
                }
                throw;
            }

            _paths.DeleteTreeIfPresent(_paths.BackupRoot);
            _paths.DeleteFileIfPresent(_paths.TransactionPath);
        }
        catch
        {
            if (!Directory.Exists(_paths.InstallRoot) &&
                Directory.Exists(_paths.BackupRoot))
            {
                _paths.MoveDirectory(_paths.BackupRoot, _paths.InstallRoot);
            }
            _paths.DeleteTreeIfPresent(_paths.StagingRoot);
            if (!Directory.Exists(_paths.BackupRoot))
            {
                _paths.DeleteFileIfPresent(_paths.TransactionPath);
            }
            throw;
        }
    }

    internal void AssertInstalledInventory()
    {
        _paths.AssertTreeSafe(_paths.InstallRoot);
        var files = Directory.EnumerateFiles(_paths.InstallRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(BundleLease.GetInstalledFileNames()) ||
            Directory.EnumerateDirectories(_paths.InstallRoot).Any())
        {
            throw new SetupSafetyException(
                "The installed API inventory is incomplete or unexpected.");
        }
    }

    private TransactionRecord? ReadTransaction()
    {
        if (!File.Exists(_paths.TransactionPath))
        {
            return null;
        }
        _paths.AssertContained(_paths.TransactionPath);
        var file = new FileInfo(_paths.TransactionPath);
        if (file.Length is <= 0 or > 1024 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SetupSafetyException(
                "The setup transaction journal is invalid.");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var root = document.RootElement;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 4 ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 4 ||
                root.GetProperty("schemaVersion").GetInt32() != 1)
            {
                throw new SetupSafetyException(
                    "The setup transaction journal has an invalid shape.");
            }
            var phase = root.GetProperty("phase").GetString();
            var treeDigest = root.GetProperty("treeDigest").ValueKind ==
                    JsonValueKind.Null
                ? null
                : root.GetProperty("treeDigest").GetString();
            if (phase is not (Preparing or Staged or Promoted) ||
                root.GetProperty("hadExisting").ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False) ||
                phase == Preparing && treeDigest is not null ||
                phase != Preparing &&
                    (treeDigest is null || treeDigest.Length != 64 ||
                     treeDigest.Any(character =>
                         character is not (>= '0' and <= '9') and
                             not (>= 'a' and <= 'f'))))
            {
                throw new SetupSafetyException(
                    "The setup transaction journal phase is invalid.");
            }
            return new TransactionRecord(
                phase,
                root.GetProperty("hadExisting").GetBoolean(),
                treeDigest);
        }
        catch (SetupSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                IOException or UnauthorizedAccessException)
        {
            throw new SetupSafetyException(
                "The setup transaction journal could not be authenticated.",
                exception);
        }
    }

    private void WriteTransaction(
        string phase,
        bool hadExisting,
        string? treeDigest)
    {
        var temporary = _paths.TransactionPath + ".tmp";
        _paths.DeleteFileIfPresent(temporary);
        var json = JsonSerializer.Serialize(
            new { schemaVersion = 1, phase, hadExisting, treeDigest }) + "\n";
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
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _paths.TransactionPath, overwrite: true);
    }

    private static void AssertRecoveryDigest(
        BundleLease.InstalledApiLease promoted,
        string? expectedDigest)
    {
        if (expectedDigest is null ||
            !string.Equals(
                promoted.Snapshot.CanonicalDigest,
                expectedDigest,
                StringComparison.Ordinal))
        {
            throw new SetupSafetyException(
                "The promoted API tree does not match the crash-recovery journal.");
        }
    }

    private sealed record TransactionRecord(
        string Phase,
        bool HadExisting,
        string? TreeDigest);
}
