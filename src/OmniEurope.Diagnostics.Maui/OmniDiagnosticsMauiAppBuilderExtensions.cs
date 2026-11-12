// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.Logs;
using OmniEurope.Diagnostics.Maui;

namespace Microsoft.Maui.Hosting;

/// <summary>Registers OmniEurope.Diagnostics in a .NET MAUI host.</summary>
public static class OmniDiagnosticsMauiAppBuilderExtensions
{
    /// <summary>
    /// Registers everything a .NET MAUI Blazor Hybrid application needs to log, time its operations and show both in
    /// its own pages, in one call from <c>MauiProgram.CreateMauiApp</c>:
    /// <list type="bullet">
    /// <item>the file logger (<c>AddOmniFileLogging</c>), writing daily compact JSON files into the <c>logs</c> folder
    /// of <c>FileSystem.AppDataDirectory</c> unless <see cref="OmniMauiDiagnosticsOptions.LogDirectory"/> or the
    /// configuration names another;</item>
    /// <item>the log reading services (<c>AddOmniLogJournal</c>) over that same folder, listing
    /// <see cref="OmniMauiDiagnosticsOptions.DefaultLogFilePattern"/> so a plain-text <c>.log</c> the host already
    /// writes stays readable;</item>
    /// <item>the performance recorder (<c>AddOmniPerformanceCollector</c>), and localization for the views;</item>
    /// <item>an initializer that checks the options and starts the recorder when the application starts: a MAUI host
    /// runs no hosted service, so nothing else would start it. An invalid option stops the application at that point,
    /// as it stops a server host;</item>
    /// <item>a <see cref="ILogFileExport"/> that offers exported log files through the system save dialog, registered
    /// only when the host has not registered its own, so the logs view shows its export and download buttons.</item>
    /// </list>
    /// The recorder and the file logger are singletons of the host's container: they are disposed with it. Each
    /// registration is safe to repeat, so the host may still call <c>AddOmniPerformanceCollector</c> or
    /// <c>AddOmniFileLogging</c> itself to adjust them.
    /// </summary>
    /// <param name="builder">The MAUI app builder.</param>
    /// <param name="configure">Optional: the log folder and patterns, and the delegates reaching the underlying
    /// options.</param>
    /// <returns><paramref name="builder"/>, to chain.</returns>
    public static MauiAppBuilder UseOmniDiagnostics(this MauiAppBuilder builder, Action<OmniMauiDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new OmniMauiDiagnosticsOptions();
        configure?.Invoke(options);

        // The data folder is only read when the options are first resolved, not while the app is being built.
        builder.Logging.AddOmniFileLogging(fileLogging =>
        {
            if (options.LogDirectory is { } directory)
                fileLogging.Directory = directory;
            else if (string.IsNullOrWhiteSpace(fileLogging.Directory))
                fileLogging.Directory = Path.Combine(FileSystem.AppDataDirectory, OmniMauiDiagnosticsOptions.DefaultLogFolderName);
            options.ConfigureFileLogging?.Invoke(fileLogging);
        });

        // LogsOptions.Directory left empty follows the file logger's folder, so both read and write the same one.
        builder.Services.AddOmniLogJournal(logs =>
        {
            if (options.LogDirectory is { } directory)
                logs.Directory = directory;
            logs.FilePattern = options.LogFilePattern
                ?? (logs.FilePattern == LogsOptions.DefaultFilePattern ? OmniMauiDiagnosticsOptions.DefaultLogFilePattern : logs.FilePattern);
            options.ConfigureLogs?.Invoke(logs);
        });

        builder.Services.AddOmniPerformanceCollector(options.ConfigurePerformance);
        builder.Services.AddLocalization();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiInitializeService, OmniDiagnosticsInitializer>());
        builder.Services.TryAddSingleton<ILogFileExport, FileSavePickerLogFileExport>();
        return builder;
    }
}
