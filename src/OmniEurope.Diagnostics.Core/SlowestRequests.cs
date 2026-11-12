// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;

namespace OmniEurope.Diagnostics;

/// <summary>
/// The slowest requests of the window, kept apart from the rolling samples: however busy the site, the
/// worst calls of the whole window survive. One bucket per hour holds that hour's <c>count</c> slowest
/// requests (a min-heap sized once, so a request costs a comparison, or a log-time swap when it ranks);
/// the slowest of the window are the slowest of the buckets' union. A new hour drops the buckets that
/// left the window, so the memory stays within <see cref="ReservedBytes"/> whether or not anyone reads it.
/// </summary>
/// <remarks>
/// Exact except at the oldest edge: once part of the oldest hour has left the window, a request that
/// ranked below that hour's top <c>count</c> is no longer known, so it cannot take an expired one's place.
/// </remarks>
internal sealed class SlowestRequests(int count, TimeSpan window)
{
    private static readonly long BucketTicks = TimeSpan.FromHours(1).Ticks;

    /// <summary>A bucket's fixed cost beside its heap array: the heap and array objects, the dictionary entry.</summary>
    private const int BucketOverheadBytes = 160;

    private readonly Dictionary<long, PriorityQueue<RequestTimingSample, double>> _buckets = new(BucketCount(window));
    private readonly Lock _gate = new();

    /// <summary>The most memory the buckets of one window hold: every hour it touches, edges included, full.
    /// Saturates at <see cref="long.MaxValue"/> if the reservation cannot fit in a <see cref="long"/>.</summary>
    public static long ReservedBytes(int count, TimeSpan window)
    {
        var bytes = (Int128)BucketCount(window) * ((long)count * Unsafe.SizeOf<(RequestTimingSample, double)>() + BucketOverheadBytes);
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }

    /// <summary>The hours a window touches, both partial edges included.</summary>
    private static int BucketCount(TimeSpan window) => (int)Math.Ceiling(window.TotalHours) + 1;

    public void Add(RequestTimingSample sample)
    {
        var key = sample.At.Ticks / BucketTicks;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                DropBucketsBefore(sample.At - window);
                _buckets[key] = bucket = new PriorityQueue<RequestTimingSample, double>(count);
            }
            if (bucket.Count < count)
                bucket.Enqueue(sample, sample.DurationMs);
            else if (bucket.TryPeek(out _, out var fastest) && sample.DurationMs > fastest)
                bucket.EnqueueDequeue(sample, sample.DurationMs);
        }
    }

    /// <summary>The <c>count</c> slowest requests at or after <paramref name="cutoff"/>, slowest first.</summary>
    public IReadOnlyList<RequestTimingSample> Since(DateTime cutoff)
    {
        lock (_gate)
        {
            DropBucketsBefore(cutoff);
            // A min-heap of the window's top `count`: memory bounded by the count, not by every bucket's content.
            var top = new PriorityQueue<RequestTimingSample, double>(count);
            foreach (var bucket in _buckets.Values)
            {
                foreach (var (sample, duration) in bucket.UnorderedItems)
                {
                    if (sample.At < cutoff) continue;
                    if (top.Count < count) top.Enqueue(sample, duration);
                    else if (top.TryPeek(out _, out var fastest) && duration > fastest) top.EnqueueDequeue(sample, duration);
                }
            }
            var slowest = new RequestTimingSample[top.Count];
            for (var i = slowest.Length - 1; i >= 0; i--)
                slowest[i] = top.Dequeue();
            return slowest;
        }
    }

    /// <summary>Drops the buckets wholly older than <paramref name="cutoff"/>; the caller holds the gate.</summary>
    private void DropBucketsBefore(DateTime cutoff)
    {
        var firstKept = cutoff.Ticks / BucketTicks;
        // Removing while enumerating is allowed on Dictionary since .NET Core 3.0: no copy of the keys.
        foreach (var key in _buckets.Keys)
        {
            if (key < firstKept)
                _buckets.Remove(key);
        }
    }
}
