// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>
/// The logger provider registered by <see cref="FileLoggingBuilderExtensions.AddOmniFileLogging(ILoggingBuilder, Action{FileLoggingOptions}?)"/>:
/// formats each entry on the caller's thread (its state, scopes and activity belong to that moment) and hands the
/// line to the background <see cref="LogFileWriter"/>. Disposed by the container, which flushes the queue.
/// </summary>
[ProviderAlias(FileLoggingBuilderExtensions.ProviderAlias)]
internal sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly LogFileWriter _writer;
    private readonly TimeProvider _time;
    private readonly LogLevel _minLevel;
    private IExternalScopeProvider? _scopes;

    /// <summary>The container's constructor: a relative folder is resolved against the content root.</summary>
    public FileLoggerProvider(IOptions<FileLoggingOptions> options, IHostEnvironment? environment = null, TimeProvider? time = null)
        : this(options.Value, environment?.ContentRootPath ?? AppContext.BaseDirectory, time ?? TimeProvider.System, Task.CompletedTask)
    {
    }

    /// <summary>The tests' constructor: the drain starts once <paramref name="drainGate"/> completes.</summary>
    internal FileLoggerProvider(FileLoggingOptions options, string contentRoot, TimeProvider time, Task drainGate)
    {
        _time = time;
        _minLevel = options.MinLevel;
        var directory = Path.GetFullPath(Path.Combine(contentRoot, options.Directory));
        _writer = new LogFileWriter(directory, new LogFileNames(options.FilePrefix, options.Instance), options, time, drainGate);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new FileLogger(name, provider), this);

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    internal IExternalScopeProvider? Scopes => _scopes;

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    internal void Write<TState>(
        string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var now = _time.GetUtcNow();
        string json;
        try
        {
            var entry = new ClefEvent(now, level, category, eventId, formatter(state, exception), exception);
            json = ClefFormatter.Format(entry, state, _scopes, Activity.Current);
        }
        catch (Exception)
        {
            // A value whose text throws: the entry is lost and counted, the caller goes on.
            _writer.CountDropped();
            return;
        }
        _writer.Enqueue(now, json);
    }
}

/// <summary>A category's logger: filters by the provider's level and passes the entry on.</summary>
internal sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
{
    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        provider.Scopes?.Push(state) ?? EmptyScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (provider.IsEnabled(logLevel))
            provider.Write(category, logLevel, eventId, state, exception, formatter);
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();

        public void Dispose()
        {
            // Nothing was pushed: no scope provider was given.
        }
    }
}
