// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>The timings of one route template and method, or of one operation, in milliseconds.</summary>
/// <param name="Method">The HTTP method; empty for an operation (see <see cref="RequestTimingSample"/>).</param>
/// <param name="Route">The route template or the operation name, as in <see cref="RequestTimingSample.Route"/>.</param>
/// <param name="Count">Requests or operations measured, an operation's failures included.</param>
/// <param name="P50Ms">Median duration.</param>
/// <param name="P95Ms">95th percentile duration.</param>
/// <param name="P99Ms">99th percentile duration.</param>
/// <param name="MaxMs">Longest duration.</param>
public sealed record RouteTimingSummary(
    string Method,
    string Route,
    int Count,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    /// <summary>True for the line of an operation the host recorded: <see cref="Method"/> is empty.</summary>
    public bool IsOperation => string.IsNullOrEmpty(Method);
}
