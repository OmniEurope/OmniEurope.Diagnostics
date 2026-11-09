// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Tells a live screen that the log files changed, so it searches again. Registered by <c>AddOmniLogs()</c> as a
/// singleton, disposed with the host.
/// <para>
/// <see cref="Changed"/> is raised when a file of <see cref="LogsOptions.Directory"/> matching
/// <see cref="LogsOptions.FilePattern"/> (never the rules file) is created, written, renamed or deleted, at most once
/// every <see cref="ThrottleMilliseconds"/>: a burst of writes gives one event at once, then one more at the end of
/// the period when more changes came meanwhile, so the last change is never missed. The folder is watched from the
/// first subscription to the last unsubscription. A folder that does not exist yet is never created: it is looked
/// for again every <see cref="RetryMilliseconds"/> until it appears, and a folder that disappears (or a watcher that
/// fails) is looked for the same way; each restart raises <see cref="Changed"/> once, since files may have changed
/// while nobody watched.
/// </para>
/// The handlers run on a file-watcher or timer thread, never the subscriber's: a UI marshals to its own context, and
/// an exception thrown by a handler is caught and ignored, and the other handlers still run.
/// </summary>
public interface ILogChanges
{
    /// <summary>The shortest time between two <see cref="Changed"/> events, in milliseconds.</summary>
    const int ThrottleMilliseconds = 2000;

    /// <summary>How often a missing folder (or a failed watcher) is looked for again, in milliseconds.</summary>
    const int RetryMilliseconds = 10000;

    /// <summary>Raised, throttled, when a log file of the folder changed. The sender is this service.</summary>
    event EventHandler? Changed;
}
