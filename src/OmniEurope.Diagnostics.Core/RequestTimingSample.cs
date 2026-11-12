// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// One timed request, under its route template (no concrete path, identifier or query value), or one operation
/// the host recorded itself.
/// </summary>
/// <remarks>
/// An operation (<see cref="RequestPerformanceRecorder.RecordOperation"/>, <see cref="RequestPerformanceRecorder.StartOperation"/>,
/// a histogram of <see cref="PerformanceOptions.Instruments"/>) is a sample whose <see cref="Method"/> is empty: its
/// <see cref="Route"/> is the operation's name as given, trimmed, case kept, and its <see cref="StatusCode"/> is
/// <see cref="OperationSucceeded"/> or <see cref="OperationFailed"/>. <see cref="IsOperation"/> and
/// <see cref="Succeeded"/> read that convention.
/// </remarks>
/// <param name="At">When the request or the operation ended, in UTC.</param>
/// <param name="Method">The HTTP method, <c>GET</c> for instance; empty for an operation.</param>
/// <param name="Route">The route template in lower case with a leading slash, <c>/orders/{id}</c> for instance; the
/// name of an operation.</param>
/// <param name="StatusCode">The response status code; <see cref="OperationSucceeded"/> or <see cref="OperationFailed"/>
/// for an operation.</param>
/// <param name="DurationMs">How long the server took to answer, or the operation took, in milliseconds.</param>
public readonly record struct RequestTimingSample(DateTime At, string Method, string Route, int StatusCode, double DurationMs)
{
    /// <summary>The <see cref="StatusCode"/> of an operation that succeeded.</summary>
    public const int OperationSucceeded = 0;

    /// <summary>The <see cref="StatusCode"/> of an operation that failed.</summary>
    public const int OperationFailed = 1;

    /// <summary>True for an operation the host recorded, false for a request: <see cref="Method"/> is empty.</summary>
    public bool IsOperation => string.IsNullOrEmpty(Method);

    /// <summary>An operation's outcome; for a request, true below a status code of 500 (a client error is the
    /// caller's, not the server's).</summary>
    public bool Succeeded => IsOperation ? StatusCode == OperationSucceeded : StatusCode < 500;
}
