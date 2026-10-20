using Clotch.Core.Credentials;
using Clotch.Core.Diagnostics;

namespace Clotch.Core.Usage;

public enum MonitorStatus
{
    Starting,
    Ok,
    NoCredentials,
    TokenExpired,
    Unauthorized,
    RateLimited,
    Offline,
    ServerError,
}

public sealed record MonitorState(
    MonitorStatus Status,
    UsageSnapshot? LastGood,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastAttempt,
    DateTimeOffset? NextPoll,
    string? SubscriptionType,
    string? RateLimitTier,
    int ConsecutiveFailures)
{
    public static readonly MonitorState Initial = new(MonitorStatus.Starting, null, null, null, null, null, null, 0);

    /// <summary>Cached numbers exist but the latest attempt did not refresh them.</summary>
    public bool IsStale => LastGood is not null && Status != MonitorStatus.Ok;
}

public sealed record MonitorOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ManualRefreshThrottle { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Local-only re-check cadence while there is no usable token (no network involved).</summary>
    public TimeSpan CredentialRecheck { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan EffectiveInterval => PollInterval < MinPollInterval ? MinPollInterval : PollInterval;
}

/// <summary>
/// Polls the usage endpoint conservatively: one request per interval, exponential backoff on failure
/// (capped), no request at all while the token is missing or expired, and a throttled manual refresh.
/// </summary>
public sealed class UsageMonitor(ICredentialsSource credentials, IUsageApi api, TimeProvider time, MonitorOptions options) : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MonitorOptions Options { get; private set; } = options;
    public MonitorState State { get; private set; } = MonitorState.Initial;
    public event Action<MonitorState>? StateChanged;

    /// <summary>Applies new options from the next cycle on; deliberately does not trigger a request.</summary>
    public void UpdateOptions(MonitorOptions o) => Options = o;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>User-initiated refresh. Returns false (and does nothing) when throttled.</summary>
    public bool RequestRefresh()
    {
        if (State.LastAttempt is { } last && time.GetUtcNow() - last < Options.ManualRefreshThrottle)
        {
            Log.Info("manual refresh throttled");
            return false;
        }
        Log.Info("manual refresh requested");
        Wake();
        return true;
    }

    /// <summary>
    /// Called when the credentials file changes. Only wakes the loop when we are waiting for a token,
    /// so a token refresh by Claude Code is picked up immediately without extra API traffic otherwise.
    /// </summary>
    public void NotifyCredentialsChanged()
    {
        if (State.Status is MonitorStatus.NoCredentials or MonitorStatus.TokenExpired or MonitorStatus.Unauthorized)
        {
            Log.Info($"credentials changed while {State.Status}; re-checking now");
            Wake();
        }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("poll failed unexpectedly", ex);
            }

            try
            {
                await _wake.WaitAsync(NextDelay(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<MonitorState> PollOnceAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var creds = credentials.Read();
            var s = State;

            if (creds is null)
            {
                return Publish(s with { Status = MonitorStatus.NoCredentials, SubscriptionType = null, RateLimitTier = null, ConsecutiveFailures = 0 }, now);
            }

            s = s with { SubscriptionType = creds.SubscriptionType, RateLimitTier = creds.RateLimitTier };

            if (creds.IsExpired(now))
            {
                // Claude Code refreshes its own token the next time it runs; we never do.
                return Publish(s with { Status = MonitorStatus.TokenExpired }, now);
            }

            var result = await api.FetchAsync(creds, ct).ConfigureAwait(false);
            now = time.GetUtcNow();
            s = s with { LastAttempt = now };

            s = result.Status switch
            {
                UsageFetchStatus.Ok => s with
                {
                    Status = MonitorStatus.Ok,
                    LastGood = result.Snapshot,
                    LastSuccess = now,
                    ConsecutiveFailures = 0,
                },
                UsageFetchStatus.Unauthorized => s with { Status = MonitorStatus.Unauthorized, ConsecutiveFailures = s.ConsecutiveFailures + 1 },
                UsageFetchStatus.RateLimited => s with { Status = MonitorStatus.RateLimited, ConsecutiveFailures = s.ConsecutiveFailures + 1 },
                UsageFetchStatus.Offline => s with { Status = MonitorStatus.Offline, ConsecutiveFailures = s.ConsecutiveFailures + 1 },
                _ => s with { Status = MonitorStatus.ServerError, ConsecutiveFailures = s.ConsecutiveFailures + 1 },
            };
            _lastRetryAfter = result.RetryAfter;
            return Publish(s, now);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private TimeSpan? _lastRetryAfter;

    /// <summary>Delay before the next poll given the current state.</summary>
    public TimeSpan NextDelay()
    {
        var s = State;
        if (s.Status is MonitorStatus.NoCredentials or MonitorStatus.TokenExpired)
            return Options.CredentialRecheck;

        var baseInterval = Options.EffectiveInterval;
        if (s.ConsecutiveFailures <= 0) return baseInterval;

        var factor = Math.Pow(2, Math.Min(s.ConsecutiveFailures, 10));
        var backoff = TimeSpan.FromTicks((long)Math.Min(baseInterval.Ticks * factor, Options.MaxBackoff.Ticks));
        if (backoff < baseInterval) backoff = baseInterval;
        if (_lastRetryAfter is { } ra && ra > backoff) backoff = ra > Options.MaxBackoff ? Options.MaxBackoff : ra;
        return backoff;
    }

    private MonitorState Publish(MonitorState s, DateTimeOffset now)
    {
        State = s;
        State = s with { NextPoll = now + NextDelay() };
        StateChanged?.Invoke(State);
        return State;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts?.Dispose();
    }
}
