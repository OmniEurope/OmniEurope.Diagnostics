// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace OmniEurope.Diagnostics;

/// <summary>The endpoint filter of the diagnostics pages: their answers are never cached.</summary>
internal static class NoStore
{
    /// <summary>Sets <c>Cache-Control: no-store</c> before the endpoint answers.</summary>
    public static ValueTask<object?> Apply(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return next(context);
    }
}
