// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>One timed request, under its route template: no concrete path, identifier or query value.</summary>
/// <param name="At">When the request ended, in UTC.</param>
/// <param name="Method">The HTTP method, <c>GET</c> for instance.</param>
/// <param name="Route">The route template in lower case with a leading slash, <c>/orders/{id}</c> for instance.</param>
/// <param name="StatusCode">The response status code.</param>
/// <param name="DurationMs">How long the server took to answer, in milliseconds.</param>
public readonly record struct RequestTimingSample(DateTime At, string Method, string Route, int StatusCode, double DurationMs);
