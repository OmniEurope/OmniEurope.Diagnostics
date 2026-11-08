// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OmniEurope.Diagnostics.Logs;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the logs pages of OmniEurope.Diagnostics.</summary>
public static class LogsEndpointRouteBuilderExtensions
{
    /// <summary>The address the pages answer on when the host names none.</summary>
    public const string DefaultPattern = "/logs";

    /// <summary>
    /// Maps the logs pages under <paramref name="pattern"/>, as one route group: <c>GET {pattern}</c> (the journal,
    /// <c>?tab=excluded</c> for the excluded entries, the filters, follow mode and the CSV export),
    /// <c>GET {pattern}/download?file=</c>, <c>GET</c> and <c>POST {pattern}/rules</c> (the exclusion rules) and
    /// <c>POST {pattern}/purge</c> (deletes old log files). Complete unstyled HTML documents, independent of the host's
    /// Blazor router, left out of the request metrics.
    /// <para>
    /// The pages show the host's logs and delete files: they MUST be protected with the returned builder
    /// (<c>app.MapOmniLogs().RequireAuthorization("Admin")</c>); one call covers every page of the group. The posts
    /// check their antiforgery token themselves, so the host needs no <c>UseAntiforgery()</c> for them.
    /// </para>
    /// Requires <see cref="Microsoft.Extensions.DependencyInjection.LogsServiceCollectionExtensions.AddOmniLogs"/>.
    /// </summary>
    /// <param name="endpoints">The host's endpoints.</param>
    /// <param name="pattern">The pages' address, <see cref="DefaultPattern"/> by default.</param>
    /// <returns>The route group, for <c>.RequireAuthorization(...)</c> and the host's other conventions.</returns>
    /// <exception cref="InvalidOperationException">The host did not call <c>AddOmniLogs()</c>: the pages would fail
    /// on their first request, so mapping them fails at startup.</exception>
    public static RouteGroupBuilder MapOmniLogs(this IEndpointRouteBuilder endpoints, string pattern = DefaultPattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (endpoints.ServiceProvider.GetService(typeof(LogsPageServices)) is null)
            throw new InvalidOperationException("The logs pages need AddOmniLogs() in the host's services.");

        var handlers = new LogsEndpointHandlers(pattern);
        var group = endpoints.MapGroup(pattern);
        group.MapGet("/", (Func<HttpContext, Task<IResult>>)handlers.Journal);
        group.MapGet("/download", (Func<HttpContext, Task<IResult>>)handlers.Download);
        group.MapGet("/rules", (Func<HttpContext, Task<IResult>>)handlers.RulesPage);
        group.MapPost("/rules", (Func<HttpContext, Task<IResult>>)handlers.PostRules);
        group.MapPost("/purge", (Func<HttpContext, Task<IResult>>)handlers.Purge);
        // Left out of the request metrics, like the performance page: reading the logs is not the site's traffic.
        group.DisableHttpMetrics();
        return group;
    }
}
