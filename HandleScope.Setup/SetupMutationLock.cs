using System.Security.Cryptography;
using System.Text;

namespace HandleScope.Setup;

internal sealed class SetupMutationLock : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly Mutex _mutex;
    private bool _held;

    private SetupMutationLock(Mutex mutex, bool held)
    {
        _mutex = mutex;
        _held = held;
    }

    internal static SetupMutationLock Acquire(
        string sid,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var waitTimeout = timeout ?? DefaultTimeout;
        if (waitTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Mutex mutex;
        try
        {
            // Product state is profile-global, so the synchronization object must
            // also span console and RDP sessions. Windows creates this object with
            // the token's default DACL; setup never broadens it and never falls
            // back to a session-local lock if access is refused.
            mutex = new Mutex(false, GetNameForSid(sid));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                WaitHandleCannotBeOpenedException)
        {
            throw new SetupSafetyException(
                "The cross-session HandleScope setup lock could not be opened safely.",
                exception);
        }
        try
        {
            bool held;
            try
            {
                held = mutex.WaitOne(waitTimeout);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }
            if (!held)
            {
                throw new SetupOperationException(
                    "Another HandleScope setup operation is still running.");
            }
            return new SetupMutationLock(mutex, held: true);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal static string GetNameForSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        return $@"Global\HandleScope.Setup.v1.{digest}";
    }

    public void Dispose()
    {
        if (_held)
        {
            _held = false;
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
