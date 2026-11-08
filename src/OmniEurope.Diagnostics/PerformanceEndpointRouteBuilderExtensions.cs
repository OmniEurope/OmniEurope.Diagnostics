// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using OmniEurope.Diagnostics.Components;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the performance page of OmniEurope.Diagnostics.</summary>
public static class PerformanceEndpointRouteBuilderExtensions
{
    /// <summary>The address the page answers on when the host names none.</summary>
    public const string DefaultPattern = "/performance";

    /// <summary>
    /// Maps <see cref="PerformancePage"/>, a complete unstyled HTML document, on <paramref name="pattern"/>.
    /// It is an ordinary endpoint, independent of the host's Blazor router. The figures describe the
    /// site: protect them with the returned builder (<c>.RequireAuthorization("Admin")</c>).
    /// Requires <see cref="Microsoft.Extensions.DependencyInjection.PerformanceServiceCollectionExtensions.AddOmniPerformance"/>.
    /// </summary>
    /// <param name="endpoints">The host's endpoints.</param>
    /// <param name="pattern">The page's address, <see cref="DefaultPattern"/> by default.</param>
    /// <exception cref="InvalidOperationException">The host registered the collector alone
    /// (<see cref="Microsoft.Extensions.DependencyInjection.PerformanceServiceCollectionExtensions.AddOmniPerformanceCollector"/>)
    /// or nothing at all: the page would fail on its first request, so mapping it fails at startup.</exception>
    public static RouteHandlerBuilder MapOmniPerformance(this IEndpointRouteBuilder endpoints, string pattern = DefaultPattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (endpoints.ServiceProvider.GetService(typeof(Microsoft.Extensions.DependencyInjection.PerformancePageServices)) is null)
            throw new InvalidOperationException(
                "The performance page needs AddOmniPerformance(): AddOmniPerformanceCollector() registers the measurement without the page.");
        // Left out of the request metrics: reading the figures must not rank the page among the slowest calls.
        return endpoints.MapGet(pattern, static () => new RazorComponentResult<PerformancePage>()).DisableHttpMetrics();
    }
}
