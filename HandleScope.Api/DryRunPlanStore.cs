using System.Collections.Concurrent;
using HandleScope.Models;

namespace HandleScope.Api;

internal sealed record AuthorizedProcessPlan(
    ProcessIdentity Identity,
    IReadOnlyList<HandleEntry> Handles);

internal sealed record DryRunPlan(
    string CanonicalKey,
    DateTimeOffset ExpiresAtUtc,
    int ProcessCount,
    int SkippedCount,
    IReadOnlyList<AuthorizedProcessPlan> Processes);

internal sealed class DryRunPlanStore
{
    private const int MaximumPlans = 32;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DryRunPlan> _plans =
        new(StringComparer.Ordinal);

    internal void Put(
        string canonicalKey,
        int processCount,
        int skippedCount,
        IReadOnlyList<AuthorizedProcessPlan> processes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        CleanupExpired();
        if (_plans.Count >= MaximumPlans)
        {
            var oldest = _plans.Values
                .OrderBy(plan => plan.ExpiresAtUtc)
                .FirstOrDefault();
            if (oldest is not null)
            {
                _plans.TryRemove(oldest.CanonicalKey, out _);
            }
        }

        _plans[canonicalKey] = new DryRunPlan(
            canonicalKey,
            DateTimeOffset.UtcNow + Lifetime,
            processCount,
            skippedCount,
            processes);
    }

    internal bool TryTake(string canonicalKey, out DryRunPlan? plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        if (!_plans.TryRemove(canonicalKey, out plan))
        {
            return false;
        }

        if (plan.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            plan = null;
            return false;
        }

        return true;
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var plan in _plans.Values)
        {
            if (plan.ExpiresAtUtc < now)
            {
                _plans.TryRemove(plan.CanonicalKey, out _);
            }
        }
    }
}

internal sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task<IDisposable?> TryEnterAsync(
        CancellationToken cancellationToken)
    {
        return await _semaphore.WaitAsync(0, cancellationToken)
            ? new Releaser(_semaphore)
            : null;
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
