// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Diagnostics;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the performance page of OmniEurope.Diagnostics in an ASP.NET Core host.</summary>
public static class PerformanceServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="PerformanceOptions"/> is read from: <c>OmniPerformance:Window</c>, say.
    /// The same section as <see cref="PerformanceCollectorServiceCollectionExtensions.ConfigurationSection"/>.</summary>
    public const string ConfigurationSection = PerformanceCollectorServiceCollectionExtensions.ConfigurationSection;

    /// <summary>
    /// Registers <see cref="RequestPerformanceRecorder"/> through
    /// <see cref="PerformanceCollectorServiceCollectionExtensions.AddOmniPerformanceCollector"/>, started with the
    /// host so it hears the first request, and what the performance page renders with (Razor components,
    /// localization). Safe to call more than once: the recorder is registered once and every
    /// <paramref name="configure"/> is applied.
    /// Pair it with <see cref="Microsoft.AspNetCore.Builder.PerformanceEndpointRouteBuilderExtensions.MapOmniPerformance"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the window, the counts kept, the routes measured and the host's own
    /// instruments; applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static IServiceCollection AddOmniPerformance(this IServiceCollection services, Action<PerformanceOptions>? configure = null)
    {
        services.AddOmniPerformanceCollector(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PerformancePageServices)))
            return services;

        services.AddLocalization();
        services.AddRazorComponents();
        services.AddSingleton<PerformancePageServices>();
        return services;
    }
}
