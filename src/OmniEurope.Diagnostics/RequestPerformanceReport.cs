// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>Pure: turns kept samples into per-route percentiles beside the slowest calls, so the arithmetic
/// is tested without a listener or a clock.</summary>
internal static class RequestPerformanceReport
{
    /// <summary>
    /// Builds the summary. <paramref name="samples"/> is the caller's own copy and is sorted in place, by
    /// route, method, then duration: each route is a contiguous, ordered run, so its percentiles are read
    /// by index and nothing beyond the copy is allocated per sample.
    /// </summary>
    public static RequestPerformanceSummary Build(
        RequestTimingSample[] samples, bool truncated, IReadOnlyList<RequestTimingSample> slowest, DateTime windowStart)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(slowest);

        DateTime? since = null;
        foreach (var sample in samples)
        {
            if (since is null || sample.At < since)
                since = sample.At;
        }

        Array.Sort(samples, static (left, right) =>
        {
            var byRoute = string.CompareOrdinal(left.Route, right.Route);
            if (byRoute != 0) return byRoute;
            var byMethod = string.CompareOrdinal(left.Method, right.Method);
            return byMethod != 0 ? byMethod : left.DurationMs.CompareTo(right.DurationMs);
        });

        var routes = new List<RouteTimingSummary>();
        for (var first = 0; first < samples.Length;)
        {
            var end = first + 1;
            while (end < samples.Length
                   && string.Equals(samples[end].Route, samples[first].Route, StringComparison.Ordinal)
                   && string.Equals(samples[end].Method, samples[first].Method, StringComparison.Ordinal))
            {
                end++;
            }
            var count = end - first;
            routes.Add(new RouteTimingSummary(
                samples[first].Method,
                samples[first].Route,
                count,
                samples[first + NearestRank(count, 0.50)].DurationMs,
                samples[first + NearestRank(count, 0.95)].DurationMs,
                samples[first + NearestRank(count, 0.99)].DurationMs,
                samples[end - 1].DurationMs));
            first = end;
        }
        routes.Sort(static (left, right) =>
        {
            var byP95 = right.P95Ms.CompareTo(left.P95Ms);
            if (byP95 != 0) return byP95;
            var byRoute = string.CompareOrdinal(left.Route, right.Route);
            return byRoute != 0 ? byRoute : string.CompareOrdinal(left.Method, right.Method);
        });

        return new RequestPerformanceSummary(windowStart, slowest, since, samples.Length, truncated, routes);
    }

    /// <summary>Nearest-rank percentile, as an index into <paramref name="count"/> ascending values: the
    /// smallest value at or above the requested share. No interpolation, so every figure is a request that
    /// happened.</summary>
    public static int NearestRank(int count, double share) =>
        Math.Clamp((int)Math.Ceiling(share * count) - 1, 0, Math.Max(count - 1, 0));
}