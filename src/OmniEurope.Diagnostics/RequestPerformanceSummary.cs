// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>What the recorder holds: the slowest calls of the window and the percentiles of every route.</summary>
/// <param name="WindowStart">Start of the window the slowest calls cover, in UTC: the window's length ago, or
/// the process start when it is more recent.</param>
/// <param name="Slowest">The slowest requests of the window, slowest first, unaffected by the sample cap.</param>
/// <param name="Since">Oldest sample the percentiles count, in UTC; later than <paramref name="WindowStart"/>
/// when the cap dropped older ones. Null without samples.</param>
/// <param name="SampleCount">Samples the percentiles are computed from.</param>
/// <param name="Truncated">True while the cap has dropped samples that would still be inside the window.</param>
/// <param name="Routes">One line per route and method, slowest 95th percentile first.</param>
public sealed record RequestPerformanceSummary(
    DateTime WindowStart,
    IReadOnlyList<RequestTimingSample> Slowest,
    DateTime? Since,
    int SampleCount,
    bool Truncated,
    IReadOnlyList<RouteTimingSummary> Routes);
