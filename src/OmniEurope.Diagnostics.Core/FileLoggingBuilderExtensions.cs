// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics;
using OmniEurope.Diagnostics.FileLogging;

namespace Microsoft.Extensions.Logging;

/// <summary>Registers the file logger of OmniEurope.Diagnostics.</summary>
public static class FileLoggingBuilderExtensions
{
    /// <summary>The configuration section <see cref="FileLoggingOptions"/> is read from: <c>OmniLogs:File:Directory</c>, say.</summary>
    public const string ConfigurationSection = "OmniLogs:File";

    /// <summary>The provider alias the logging filters name: <c>Logging:OmniFile:LogLevel:Default</c>, say.</summary>
    public const string ProviderAlias = "OmniFile";

    /// <summary>
    /// Adds a logger that writes every entry to daily files of the compact Serilog JSON format (one JSON object per
    /// line: <c>@t</c>, <c>@l</c>, <c>@mt</c>, <c>@m</c>, <c>@x</c>, <c>@tr</c>, <c>@sp</c>, the category as
    /// <c>SourceContext</c>, the structured and scope properties at the top level), through a bounded background
    /// queue, so logging never waits for the disk. <see cref="FileLoggingOptions"/> are bound to
    /// <see cref="ConfigurationSection"/> and checked when the host starts; the logging filters apply under the
    /// <see cref="ProviderAlias"/> alias. Safe to call more than once: the logger is registered once and every
    /// <paramref name="configure"/> is applied.
    /// </summary>
    /// <param name="builder">The host's logging builder.</param>
    /// <param name="configure">Optional: the folder, file prefix, instance, level, retention, file size and queue
    /// capacity; applied after the <see cref="ConfigurationSection"/> section, so it has the last word.</param>
    public static ILoggingBuilder AddOmniFileLogging(this ILoggingBuilder builder, Action<FileLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var first = !services.Any(descriptor =>
            descriptor.ServiceType == typeof(ILoggerProvider) && descriptor.ImplementationType == typeof(FileLoggerProvider));

        // Bound once, ahead of every configure delegate: appsettings, environment variables
        // (OmniLogs__File__Directory) and the host's other sources set the values, code then overrides them.
        if (first)
            services.AddOptions<FileLoggingOptions>().BindConfiguration(ConfigurationSection).ValidateOnStart();
        if (configure is not null)
            services.Configure(configure);
        if (!first)
            return builder;

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FileLoggingOptions>, FileLoggingOptionsValidator>());
        // The logs pages read this logger's folder when the host names none (OmniLogs:Directory left empty).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<LogsOptions>, LogsOptionsFromFileLogging>());
        // A singleton of the container, so the host's shutdown disposes it and flushes the queue.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        return builder;
    }

    /// <summary>
    /// Adds the file logger writing to <paramref name="directory"/>, the other
    /// <see cref="FileLoggingOptions"/> from <see cref="ConfigurationSection"/> or their defaults; see
    /// <see cref="AddOmniFileLogging(ILoggingBuilder, Action{FileLoggingOptions}?)"/>.
    /// </summary>
    /// <param name="builder">The host's logging builder.</param>
    /// <param name="directory">The folder the files are written to; a relative path is relative to the content root.</param>
    public static ILoggingBuilder AddOmniFileLogging(this ILoggingBuilder builder, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return builder.AddOmniFileLogging(options => options.Directory = directory);
    }
}
