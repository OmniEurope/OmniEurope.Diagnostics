// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.Logs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the log reading services of OmniEurope.Diagnostics, in any host.</summary>
public static class LogJournalServiceCollectionExtensions
{
    /// <summary>The configuration section <see cref="LogsOptions"/> is read from: <c>OmniLogs:Directory</c>, say.</summary>
    public const string ConfigurationSection = "OmniLogs";

    /// <summary>
    /// Registers what reads, filters and watches the host's log files, without any page, form or antiforgery:
    /// <see cref="LogsOptions"/> (bound to <see cref="ConfigurationSection"/>, checked when a host starts, or when
    /// they are first read in a host that runs no hosted service), the log folder (a relative
    /// <see cref="LogsOptions.Directory"/> is resolved against <see cref="IHostEnvironment.ContentRootPath"/>, or
    /// against <see cref="AppContext.BaseDirectory"/> when no host environment is registered), localization, and the
    /// public services: <see cref="ILogJournal"/> (scoped), <see cref="ILogRuleStore"/> (the JSON rules file, a
    /// singleton) and <see cref="ILogChanges"/> (a singleton). Each public service is registered only when the host
    /// has not registered its own; a host's registration made after this call wins too (the last registration is
    /// the one resolved), so a host can keep its rules in a database whatever the order. A host-provided
    /// <see cref="ILogRuleStore"/> may be scoped. The ASP.NET Core package's <c>AddOmniLogs</c> adds its pages on top
    /// of this call. Safe to call more than once: the services are registered once and every
    /// <paramref name="configure"/> is applied.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Optional: the folder, pattern, rules file, page size, read cap and follow period;
    /// applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static IServiceCollection AddOmniLogJournal(this IServiceCollection services, Action<LogsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var first = !services.Any(descriptor => descriptor.ServiceType == typeof(LogJournalServices));

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
        services.AddSingleton<LogJournalServices>();
        services.AddSingleton(provider => new LogDirectory(
            provider.GetRequiredService<IOptions<LogsOptions>>(),
            provider.GetService<IHostEnvironment>()));
        services.TryAddSingleton<ILogRuleStore, LogRuleStore>();
        services.TryAddScoped<ILogJournal>(provider => new LogJournal(
            provider.GetRequiredService<LogDirectory>(),
            provider.GetRequiredService<IOptions<LogsOptions>>(),
            provider.GetRequiredService<ILogRuleStore>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<ILogChanges>(provider => new LogChangeWatcher(
            provider.GetRequiredService<LogDirectory>(),
            provider.GetRequiredService<IOptions<LogsOptions>>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}

/// <summary>Marks a host registered by <see cref="LogJournalServiceCollectionExtensions.AddOmniLogJournal"/>, so a second
/// call registers nothing twice.</summary>
internal sealed class LogJournalServices;
