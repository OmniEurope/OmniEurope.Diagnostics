// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.Logs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the logs pages of OmniEurope.Diagnostics.</summary>
public static class LogsServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="LogsOptions"/> is read from: <c>OmniLogs:Directory</c>, say.</summary>
    public const string ConfigurationSection = "OmniLogs";

    /// <summary>
    /// Registers what the logs pages read, filter and render with: <see cref="LogsOptions"/> (bound to
    /// <see cref="ConfigurationSection"/> and checked when the host starts), the log folder, the exclusion rules,
    /// Razor components, localization and antiforgery (every form of the pages carries a token the posts check).
    /// Safe to call more than once: the services are registered once and every <paramref name="configure"/> is
    /// applied. Pair it with <see cref="Microsoft.AspNetCore.Builder.LogsEndpointRouteBuilderExtensions.MapOmniLogs"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the folder, pattern, rules file, page size, read cap and follow period;
    /// applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static IServiceCollection AddOmniLogs(this IServiceCollection services, Action<LogsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var first = !services.Any(descriptor => descriptor.ServiceType == typeof(LogsPageServices));

        // Bound once, ahead of every configure delegate: appsettings, environment variables
        // (OmniLogs__Directory) and the host's other sources set the values, code then overrides them.
        if (first)
            services.AddOptions<LogsOptions>().BindConfiguration(ConfigurationSection).ValidateOnStart();
        if (configure is not null)
            services.Configure(configure);
        if (!first)
            return services;

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LogsOptions>, LogsOptionsValidator>());
        services.AddLocalization();
        services.AddRazorComponents();
        services.AddAntiforgery();
        services.AddSingleton<LogsPageServices>();
        services.AddSingleton<LogDirectory>();
        services.AddSingleton<LogRuleStore>();
        services.AddScoped<LogsPageState>();
        return services;
    }
}
