// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the performance recorder of OmniEurope.Diagnostics, in any host.</summary>
public static class PerformanceCollectorServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="PerformanceOptions"/> is read from: <c>OmniPerformance:Window</c>, say.</summary>
    public const string ConfigurationSection = "OmniPerformance";

    /// <summary>
    /// Registers <see cref="RequestPerformanceRecorder"/> alone, as a singleton and a hosted service started with
    /// the host so it hears the first request. A host that runs no hosted service (a .NET MAUI application) calls
    /// <see cref="RequestPerformanceRecorder.Start"/> itself once its services are built; the operations it records
    /// directly are kept either way. The figures are read through <see cref="RequestPerformanceRecorder.Summarize"/>
    /// or shown by the <see cref="OmniEurope.Diagnostics.Components.OmniPerformanceView"/> component, which also
    /// needs <c>AddLocalization()</c>. The ASP.NET Core package's <c>AddOmniPerformance</c> adds its page on top of
    /// this call. Safe to call more than once: the recorder is registered once and every
    /// <paramref name="configure"/> is applied.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the window, the counts kept, the routes measured and the host's own
    /// instruments; applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
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
