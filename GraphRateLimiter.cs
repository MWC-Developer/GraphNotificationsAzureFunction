/*
 * By David Barrett, Microsoft Ltd. Use at your own risk.  No warranties are given.
 * 
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * */

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

/// <summary>
/// Process-wide, singleton rate limiter for all outbound Graph API calls.
///
/// Two layers of protection are combined:
///
/// 1. <b>Proactive</b> — a <see cref="SlidingWindowRateLimiter"/> enforces the configured
///    permits-per-window ceiling <em>before</em> each request is sent, queuing callers that
///    would otherwise exceed the budget.
///
/// 2. <b>Reactive</b> — when Graph returns HTTP 429 (Too Many Requests) or 503 (Service
///    Unavailable), <see cref="NotifyThrottled"/> records the earliest point-in-time at which
///    new requests are safe again (from the <c>Retry-After</c> response header). All callers
///    waiting in <see cref="AcquireAsync"/> will honour that embargo before proceeding.
/// </summary>
public sealed class GraphRateLimiter : IDisposable
{
    // Default fallback delay when Graph returns a 429/503 without a Retry-After header.
    private static readonly TimeSpan DefaultThrottleDelay = TimeSpan.FromSeconds(30);

    private readonly SlidingWindowRateLimiter _limiter;
    private readonly ILogger<GraphRateLimiter> _logger;

    // Ticks (UTC) of the wall-clock time after which calls are safe again, stored as long for Interlocked.
    private long _throttledUntilTicks = DateTimeOffset.MinValue.UtcTicks;

    public GraphRateLimiter(GraphSubscriptionSettings settings, ILogger<GraphRateLimiter> logger)
    {
        _logger = logger;

        // Divide the window evenly into 4 segments for a smooth sliding window.
        var segmentCount = 4;
        var windowSeconds = Math.Max(1, settings.RateLimitWindowSeconds);
        var permits = Math.Max(1, settings.RateLimitPermits);

        _limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit            = permits,
            Window                 = TimeSpan.FromSeconds(windowSeconds),
            SegmentsPerWindow      = segmentCount,
            QueueProcessingOrder   = QueueProcessingOrder.OldestFirst,
            // Queue up to one full window's worth of calls so bursts are smoothed
            // rather than rejected immediately.
            QueueLimit             = permits,
            AutoReplenishment      = true
        });

        _logger.LogInformation(
            "Graph rate limiter initialised: {Permits} permits per {Window} s (4 segments).",
            permits, windowSeconds);
    }

    /// <summary>
    /// Acquires one permit from the rate limiter, waiting if necessary.
    /// Also blocks until any active throttle embargo has elapsed.
    /// </summary>
    /// <exception cref="OperationCanceledException">Thrown if the cancellation token fires while waiting.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the rate limiter's queue is full and the permit is rejected.</exception>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        // Honour any active reactive throttle embargo first.
        var embargoTicks = Interlocked.Read(ref _throttledUntilTicks);
        var embargo = new DateTimeOffset(embargoTicks, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        if (embargo > now)
        {
            var delay = embargo - now;
            _logger.LogInformation(
                "Graph throttle embargo active; delaying outbound call by {DelayMs} ms.", (long)delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // Acquire a proactive permit from the sliding window.
        using var lease = await _limiter.AcquireAsync(permitCount: 1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException(
                "Graph rate limiter queue is full. The system is overloaded; the request cannot be dispatched.");
        }
    }

    /// <summary>
    /// Signals that Graph returned a throttle response (429 / 503).
    /// Parses the <c>Retry-After</c> header and sets a process-wide embargo so all
    /// concurrent callers wait before their next attempt.
    /// </summary>
    public void NotifyThrottled(HttpResponseMessage response)
    {
        var delay = ParseRetryAfter(response) ?? DefaultThrottleDelay;
        var retryAt = DateTimeOffset.UtcNow.Add(delay);

        var retryAtTicks = retryAt.UtcTicks;

        // Only advance the embargo; never move it backward (two concurrent 429s, take the later one).
        long current;
        do
        {
            current = Interlocked.Read(ref _throttledUntilTicks);
            if (retryAtTicks <= current)
            {
                break;  // already set to something later
            }
        }
        while (Interlocked.CompareExchange(ref _throttledUntilTicks, retryAtTicks, current) != current);

        _logger.LogWarning(
            "Graph throttle response received (HTTP {Status}). Retry-After delay: {DelayMs} ms. Embargo until {RetryAt:u}.",
            (int)response.StatusCode,
            (long)delay.TotalMilliseconds,
            retryAt);
    }

    public void Dispose() => _limiter.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        // Retry-After may be expressed as a delta-seconds integer or an HTTP-date.
        if (header.Delta.HasValue)
        {
            return header.Delta.Value;
        }

        if (header.Date.HasValue)
        {
            var remaining = header.Date.Value - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }
}
