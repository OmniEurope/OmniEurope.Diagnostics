// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics.Maui;

/// <summary>
/// What <see cref="Microsoft.Maui.Hosting.OmniDiagnosticsMauiAppBuilderExtensions.UseOmniDiagnostics"/> adds to a MAUI host, beyond the defaults a desktop
/// application wants: where the logs live and which files the logs view lists. The three delegates reach the options
/// of the underlying services for everything else (retention, file size, window, instruments); they run after the
/// configuration sections and after the defaults of this class, so they have the last word.
/// </summary>
public sealed class OmniMauiDiagnosticsOptions
{
    /// <summary>The default <see cref="LogFilePattern"/>: the compact JSON files of the file logger, and the plain-text
    /// <c>.log</c> files a host may already write (a crash log, say), read as plain text.</summary>
    public const string DefaultLogFilePattern = "*.json;*.log";

    /// <summary>The name of the folder, inside the application data folder, the logs are written to by default.</summary>
    public const string DefaultLogFolderName = "logs";

    /// <summary>
    /// The folder the log files are written to and read from; <see langword="null"/> (the default) means
    /// <c>logs</c> inside <c>FileSystem.AppDataDirectory</c>, unless the host's configuration names one
    /// (<c>OmniLogs:File:Directory</c>). A value set here wins over the configuration. The folder is created when
    /// missing, when the first log entry is written.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// The file-name patterns of the log files the logs view lists, separated by <c>;</c>:
    /// <see cref="DefaultLogFilePattern"/> by default, unless the host's configuration names another one
    /// (<c>OmniLogs:FilePattern</c>). A value set here wins over the configuration.
    /// </summary>
    public string? LogFilePattern { get; set; }

    /// <summary>Optional: adjusts the file logger (level, retention, file size, queue capacity, file prefix).</summary>
    public Action<FileLoggingOptions>? ConfigureFileLogging { get; set; }

    /// <summary>Optional: adjusts the log reading services (page size, read cap, rules file, follow period).</summary>
    public Action<LogsOptions>? ConfigureLogs { get; set; }

    /// <summary>Optional: adjusts the performance recorder (window, slowest count, memory limit, the host's own
    /// instruments).</summary>
    public Action<PerformanceOptions>? ConfigurePerformance { get; set; }
}
