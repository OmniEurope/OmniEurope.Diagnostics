// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Hosting;

namespace OmniEurope.Diagnostics;

/// <summary>Finds the folder a relative log folder is resolved against.</summary>
internal static class HostContentRoot
{
    /// <summary>
    /// The host's content root, or <see cref="AppContext.BaseDirectory"/> when there is no host environment or when
    /// it has no content root to give: the <see cref="IHostEnvironment"/> a .NET MAUI application registers throws
    /// <see cref="NotImplementedException"/> from <see cref="IHostEnvironment.ContentRootPath"/>.
    /// </summary>
    public static string Of(IHostEnvironment? environment)
    {
        if (environment is null) return AppContext.BaseDirectory;
        try
        {
            return environment.ContentRootPath ?? AppContext.BaseDirectory;
        }
        catch (NotImplementedException)
        {
            return AppContext.BaseDirectory;
        }
    }
}
