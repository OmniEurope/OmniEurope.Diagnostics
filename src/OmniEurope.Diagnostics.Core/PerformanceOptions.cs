// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// What <see cref="RequestPerformanceRecorder"/> keeps, for how long and in how much memory. Configured
/// through <see cref="Microsoft.Extensions.DependencyInjection.PerformanceCollectorServiceCollectionExtensions.AddOmniPerformanceCollector"/> (or the ASP.NET Core package's <c>AddOmniPerformance</c>),
/// checked when the recorder is created.
/// </summary>
public sealed class PerformanceOptions
{
    /// <summary>
    /// How far back the figures go: 7 days by default. The window lives in memory and restarts with the process.
    /// A window reaching back before the first calendar date is refused when the recorder is created.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How many of the window's slowest requests the page lists: 20 by default. They are kept apart from the
    /// rolling samples, so a busy site never pushes one of the worst calls out of the list.
    /// </summary>
    public int SlowestCount { get; set; } = 20;

    /// <summary>
    /// The memory the recorder may hold, in bytes: 2 MiB by default. The slowest list takes its share first
    /// (<see cref="SlowestCount"/> requests per hour of the window), the rest is a ring of requests for the
    /// per-route percentiles, allocated once: when it is full, the newest request replaces the oldest.
    /// </summary>
    public long MemoryLimitBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Decides whether a route template (lower case with a leading slash, <c>/api/orders/{id}</c>) is measured. It never applies to the operations the host records.
    /// Null keeps every templated route except the health probes, the framework and static files
    /// (see <see cref="RequestPerformanceRecorder.IsMeasuredByDefault"/>).
    /// </summary>
    public Func<string, bool>? RouteFilter { get; set; }

    /// <summary>
    /// The host's own histograms whose measurements are recorded as operations: empty by default. Each names its
    /// meter, its histogram and the tag carrying the operation name; see <see cref="PerformanceInstrument"/> for the
    /// units, the scope and the failure tags. Read when the recorder is created. In configuration:
    /// <c>OmniPerformance:Instruments:0:MeterName</c>, say.
    /// </summary>
    public IList<PerformanceInstrument> Instruments { get; } = [];
}
