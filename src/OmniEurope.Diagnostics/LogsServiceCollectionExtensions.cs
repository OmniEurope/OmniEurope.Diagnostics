// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.Logs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the logs pages of OmniEurope.Diagnostics in an ASP.NET Core host.</summary>
public static class LogsServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="LogsOptions"/> is read from: <c>OmniLogs:Directory</c>, say.
    /// The same section as <see cref="LogJournalServiceCollectionExtensions.ConfigurationSection"/>.</summary>
    public const string ConfigurationSection = LogJournalServiceCollectionExtensions.ConfigurationSection;

    /// <summary>
    /// Registers what the logs pages read, filter and render with: the services of
    /// <see cref="LogJournalServiceCollectionExtensions.AddOmniLogJournal"/> (<see cref="LogsOptions"/> bound to
    /// <see cref="ConfigurationSection"/> and checked when the host starts, the log folder, the exclusion rules,
    /// localization, and the public <see cref="ILogJournal"/>, <see cref="ILogRuleStore"/> and
    /// <see cref="ILogChanges"/> a host builds its own screen with, each registered only when the host has not
    /// registered its own), then Razor components and antiforgery (every form of the pages carries a token the
    /// posts check). Safe to call more than once: the services are registered once and every
    /// <paramref name="configure"/> is applied. Pair it with
    /// <see cref="Microsoft.AspNetCore.Builder.LogsEndpointRouteBuilderExtensions.MapOmniLogs"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the folder, pattern, rules file, page size, read cap and follow period;
    /// applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static IServiceCollection AddOmniLogs(this IServiceCollection services, Action<LogsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOmniLogJournal(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(LogsPageServices)))
            return services;

        services.AddRazorComponents();
        services.AddAntiforgery();
        services.AddSingleton<LogsPageServices>();
        services.AddScoped<LogsPageState>();
        return services;
    }
}
