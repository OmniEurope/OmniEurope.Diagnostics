// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the performance measurement of OmniEurope.Diagnostics.</summary>
public static class PerformanceServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="PerformanceOptions"/> is read from: <c>OmniPerformance:Window</c>, say.</summary>
    public const string ConfigurationSection = "OmniPerformance";

    /// <summary>
    /// Registers <see cref="RequestPerformanceRecorder"/>, started with the host so it hears the first
    /// request, and what the performance page renders with (Razor components, localization). Safe to call
    /// more than once: the recorder is registered once and every <paramref name="configure"/> is applied.
    /// Pair it with <see cref="Microsoft.AspNetCore.Builder.PerformanceEndpointRouteBuilderExtensions.MapOmniPerformance"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the window, the counts kept and the routes measured; applied after
    /// the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
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

    /// <summary>
    /// Registers <see cref="RequestPerformanceRecorder"/> alone, without the page: for a host that shows the
    /// figures itself (an API serving <see cref="RequestPerformanceRecorder.Summarize"/>, a page of its own), so
    /// it carries no Razor component or localization services it does not use.
    /// <see cref="Microsoft.AspNetCore.Builder.PerformanceEndpointRouteBuilderExtensions.MapOmniPerformance"/>
    /// refuses such a host: the page needs <see cref="AddOmniPerformance"/>. Safe to call more than once, and
    /// beside <see cref="AddOmniPerformance"/>: the recorder is registered once and every
    /// <paramref name="configure"/> is applied.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the window, the counts kept and the routes measured; applied after
    /// the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static IServiceCollection AddOmniPerformanceCollector(this IServiceCollection services, Action<PerformanceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var first = !services.Any(descriptor => descriptor.ServiceType == typeof(RequestPerformanceRecorder));

        // Bound once, ahead of every configure delegate: appsettings, environment variables
        // (OmniPerformance__Window) and the host's other sources set the values, code then overrides them.
        if (first)
            services.AddOptions<PerformanceOptions>().BindConfiguration(ConfigurationSection);
        if (configure is not null)
            services.Configure(configure);
        if (!first)
            return services;

        services.AddMetrics();
        services.AddSingleton(provider => new RequestPerformanceRecorder(
            provider.GetRequiredService<IOptions<PerformanceOptions>>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddHostedService(provider => provider.GetRequiredService<RequestPerformanceRecorder>());
        return services;
    }
}
