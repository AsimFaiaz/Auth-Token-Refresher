// TokenRefresher.cs
// Universal token cache + proactive refresh (skew + jitter) with single-flight coordination.
// Author: Asim Faiaz 
// License: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Demo.Auth
{
    //Simple token value + expiry
    public readonly struct AccessToken
    {
        public string Value { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public AccessToken(string value, DateTimeOffset expiresAtUtc)
            => (Value, ExpiresAtUtc) = (value, expiresAtUtc);
        public bool IsExpired(DateTimeOffset? now = null) =>
            (now ?? DateTimeOffset.UtcNow) >= ExpiresAtUtc;
    }

    //Callback that fetches a fresh token from any source you like
    public delegate Task<AccessToken> FetchTokenAsync(CancellationToken ct);

    /* ==========================================================================
     * Caches an access token and refreshes it proactively before expiry.
     * Single-flight refresh: only one refresh runs; other callers await it.
     * Proactive window: refresh ahead of expiry by a 'skew' (+ small jitter).
     * Optional minimal refresh interval to avoid hammering your STS.
    =============================================================================*/ 
    public sealed class TokenRefresher
    {
        private readonly FetchTokenAsync _fetch;
        private readonly TimeSpan _refreshSkew;
        private readonly TimeSpan _minRefreshInterval;
        private readonly double _jitterRatio;
        private readonly Random _rng = new();

        private AccessToken? _cached;
        private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

        // Single-flight coordination
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<AccessToken>? _inflight;

        public TokenRefresher(
            FetchTokenAsync fetchToken,
            TimeSpan? refreshSkew = null,              // e.g. 60s: refresh 60s before expiry
            TimeSpan? minimumRefreshInterval = null,   // e.g. 5s: don't refetch more often than this
            double jitterRatio = 0.15                  // add up to 15% jitter to skew
        )
        {
            _fetch = fetchToken ?? throw new ArgumentNullException(nameof(fetchToken));
            _refreshSkew = refreshSkew ?? TimeSpan.FromSeconds(60);
            _minRefreshInterval = minimumRefreshInterval ?? TimeSpan.FromSeconds(5);
            _jitterRatio = Math.Clamp(jitterRatio, 0, 0.5);
        }

        /* ================================================================================
         * Returns a valid token. Refreshes proactively if we're within the skew window,
         * or if no token exists, or if it's expired.
         * Safe to call concurrently; refresh is single-flighted.
         ==================================================================================*/
        public async Task<AccessToken> GetValidTokenAsync(CancellationToken ct = default)
        {
            // Fast: if we clearly have time left, return cached
            var now = DateTimeOffset.UtcNow;
            if (_cached is AccessToken t && !NeedsRefresh(t, now))
                return t;

            // Slow: maybe refresh (single-flight)
            var task = await EnsureRefreshTaskAsync(now, ct).ConfigureAwait(false);
            var fresh = await task.ConfigureAwait(false);
            return fresh;
        }

        private bool NeedsRefresh(AccessToken token, DateTimeOffset now)
        {
            if (token.IsExpired(now)) return true;

            // Proactive window = expiry - (skew + jitter)
            var jitter = TimeSpan.FromMilliseconds(_rng.NextDouble() * _jitterRatio * _refreshSkew.TotalMilliseconds);
            var refreshPoint = token.ExpiresAtUtc - (_refreshSkew + jitter);
            return now >= refreshPoint;
        }

        private async Task<Task<AccessToken>> EnsureRefreshTaskAsync(DateTimeOffset now, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If another thread already started a refresh, just await it
                if (_inflight is Task<AccessToken> inflight) return inflight;

                // Avoid hammering: if we refreshed very recently and still have a token, reuse it
                if (_cached is AccessToken cached &&
                    (now - _lastRefreshUtc) < _minRefreshInterval &&
                    !cached.IsExpired(now))
                {
                    return Task.FromResult(cached);
                }

                // Start a new refresh task
                _inflight = RefreshCoreAsync(ct);
                return _inflight;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<AccessToken> RefreshCoreAsync(CancellationToken ct)
        {
            try
            {
                var fresh = await _fetch(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(fresh.Value))
                    throw new InvalidOperationException("FetchTokenAsync returned an empty token value.");

                // Update cache
                _cached = fresh;
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                return fresh;
            }
            finally
            {
                // Clear inflight marker
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try { _inflight = null; } finally { _gate.Release(); }
            }
        }
    }
}
