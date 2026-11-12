// SPDX-License-Identifier: EUPL-1.2
using System.IO.Enumeration;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The default <see cref="ILogChanges"/>: a <see cref="FileSystemWatcher"/> on the log folder, started on the first
/// subscription and stopped on the last unsubscription, throttled and retried with <see cref="TimeProvider"/> timers.
/// </summary>
internal sealed class LogChangeWatcher : ILogChanges, IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(ILogChanges.ThrottleMilliseconds);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromMilliseconds(ILogChanges.RetryMilliseconds);

    private readonly Lock _gate = new();
    private readonly string _folder;
    private readonly IReadOnlyList<string> _patterns;
    private readonly string _rulesFile;
    private readonly TimeProvider _time;
    private EventHandler? _changed;
    private FileSystemWatcher? _watcher;
    private ITimer? _retry;
    private ITimer? _pending;
    private DateTimeOffset? _lastRaised;
    private bool _disposed;

    /// <summary>Watches the folder of <paramref name="directory"/> for the files of <paramref name="options"/>.</summary>
    public LogChangeWatcher(LogDirectory directory, IOptions<LogsOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _folder = directory.FullPath;
        _patterns = LogDirectory.Patterns(options.Value.FilePattern);
        _rulesFile = options.Value.RulesFile;
        _time = time;
    }

    /// <summary>True while a file watcher runs on the folder.</summary>
    internal bool Watching
    {
        get
        {
            lock (_gate)
                return _watcher is not null;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                if (_disposed || value is null)
                    return;
                var first = _changed is null;
                _changed += value;
                if (first && !TryStartLocked())
                    ScheduleRetryLocked();
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                    StopLocked();
            }
        }
    }

    /// <summary>A file event: raises <see cref="ILogChanges.Changed"/> (throttled) when either name is a log file.</summary>
    internal void OnFileEvent(string? name, string? oldName = null)
    {
        if (IsLogFile(name) || IsLogFile(oldName))
            Signal();
    }

    /// <summary>The watcher failed (its buffer overflowed, the folder went away): changes may have been missed, so
    /// subscribers are told, and the folder is watched again once it can be.</summary>
    /// <param name="source">The watcher that failed; an older one already replaced is left alone. Null: the current one.</param>
    internal void OnWatcherError(object? source = null)
    {
        lock (_gate)
        {
            if (source is not null && !ReferenceEquals(source, _watcher))
                return;
            _watcher?.Dispose();
            _watcher = null;
            if (_changed is not null)
                ScheduleRetryLocked();
        }
        Signal();
    }

    /// <summary>Looks for the folder again; once watched, tells the subscribers, since files may have changed.</summary>
    internal void Retry()
    {
        lock (_gate)
        {
            if (_changed is null || _watcher is not null || !TryStartLocked())
                return;
            _retry?.Dispose();
            _retry = null;
        }
        Signal();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _changed = null;
            StopLocked();
        }
    }

    private bool IsLogFile(string? name) =>
        !string.IsNullOrEmpty(name)
        && !name.StartsWith(_rulesFile, StringComparison.OrdinalIgnoreCase)
        && _patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name));

    private void Signal()
    {
        lock (_gate)
        {
            if (_changed is null || _pending is not null)
                return;
            var now = _time.GetUtcNow();
            if (_lastRaised is { } last && now - last < Throttle)
            {
                // Within the period: one more event at its end carries every change made meanwhile.
                _pending = _time.CreateTimer(_ => RaisePending(), null, last + Throttle - now, Timeout.InfiniteTimeSpan);
                return;
            }
            _lastRaised = now;
        }
        Raise();
    }

    private void RaisePending()
    {
        lock (_gate)
        {
            _pending?.Dispose();
            _pending = null;
            if (_changed is null)
                return;
            _lastRaised = _time.GetUtcNow();
        }
        Raise();
    }

    private void Raise()
    {
        EventHandler? handlers;
        lock (_gate)
            handlers = _changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A failing subscriber must neither stop the host (this runs on a watcher or timer thread) nor starve the others.
            }
        }
    }

    private bool TryStartLocked()
    {
        if (!Directory.Exists(_folder))
            return false;
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(_folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            foreach (var pattern in _patterns)
                watcher.Filters.Add(pattern);
            watcher.Created += (_, e) => OnFileEvent(e.Name);
            watcher.Changed += (_, e) => OnFileEvent(e.Name);
            watcher.Deleted += (_, e) => OnFileEvent(e.Name);
            watcher.Renamed += (_, e) => OnFileEvent(e.Name, e.OldName);
            watcher.Error += (sender, _) => OnWatcherError(sender);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The folder went away between the check and the watch, or cannot be watched: looked for again later.
            watcher?.Dispose();
            return false;
        }
    }

    private void ScheduleRetryLocked() =>
        _retry ??= _time.CreateTimer(_ => Retry(), null, RetryPeriod, RetryPeriod);

    private void StopLocked()
    {
        _watcher?.Dispose();
        _watcher = null;
        _retry?.Dispose();
        _retry = null;
        _pending?.Dispose();
        _pending = null;
    }
}
