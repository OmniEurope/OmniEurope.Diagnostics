// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The log folder of <see cref="LogsOptions"/>, read the way the logs pages read it, for a host that builds its own
/// screen (its own UI, an API). Registered by <c>AddOmniLogs()</c> as a scoped service (so a scoped
/// <see cref="ILogRuleStore"/> works): resolve it from a request or from a scope you create. Only the files directly
/// in <see cref="LogsOptions.Directory"/> that match <see cref="LogsOptions.FilePattern"/> are listed, read, opened
/// and purged, never the rules file nor a sub-folder.
/// <para>
/// The methods are named <c>*Async</c> but do their file I/O synchronously on the calling thread: a host that must
/// not block calls them from a background task.
/// </para>
/// </summary>
public interface ILogJournal
{
    /// <summary>The log files, the most recently written first; an absent folder has none.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    Task<IReadOnlyList<LogFileInfo>> ListFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry <paramref name="search"/> accepts, newest first, read within <see cref="LogsOptions.MaxBytesRead"/>
    /// (the newest files first, a larger file from its end), split between the journal and the excluded tab by the
    /// enabled rules of <see cref="ILogRuleStore"/>. The caller pages and sorts the entries itself.
    /// </summary>
    /// <param name="search">The file, filters and tab.</param>
    /// <param name="cancellationToken">Cancels the search: checked before each file and each line read.</param>
    Task<LogSearchResult> SearchAsync(LogSearch search, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the listed file <paramref name="name"/> for reading, shared for writing and deleting so the host's log
    /// writer is never blocked; the caller disposes the stream. Null when <paramref name="name"/> is not a bare file
    /// name of the listing (a path, a parent, another folder's file), or when the file is gone. A file that exists
    /// but cannot be opened (on Windows: held without read sharing) faults the task with its
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <param name="name">The file's name, as <see cref="ListFilesAsync"/> gives it (case ignored).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<Stream?> OpenFileAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the log files last written more than <paramref name="olderThanDays"/> days ago; the rules file and the
    /// files the pattern does not match are never touched. A file that cannot be deleted (on Windows: held open
    /// without delete sharing, or read-only) is counted as failed and stays.
    /// </summary>
    /// <param name="olderThanDays">The age, in days, from 0 (every file written before now).</param>
    /// <param name="cancellationToken">Cancels the purge before the next file.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThanDays"/> is negative.</exception>
    Task<LogPurgeResult> PurgeAsync(int olderThanDays, CancellationToken cancellationToken = default);
}

/// <summary>A log file of the folder.</summary>
/// <param name="Name">Its name, without a path.</param>
/// <param name="Length">Its size, in bytes.</param>
/// <param name="LastWriteUtc">When it was last written, in UTC.</param>
public sealed record LogFileInfo(string Name, long Length, DateTimeOffset LastWriteUtc);

/// <summary>What <see cref="ILogJournal.SearchAsync"/> looks for; every filter left null keeps everything.</summary>
public sealed record LogSearch
{
    /// <summary>The one file read (a name of <see cref="ILogJournal.ListFilesAsync"/>, case ignored), or null for
    /// every file. A name that is not a listed bare file name reads nothing.</summary>
    public string? File { get; init; }

    /// <summary>The lowest level kept, or null for every level.</summary>
    public LogSeverity? MinLevel { get; init; }

    /// <summary>The earliest instant kept, included, or null.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>The latest instant kept, included, or null.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Text the message, the category, the exception or the correlation id contains (case ignored), or null.</summary>
    public string? Text { get; init; }

    /// <summary>False for the journal tab (the entries no enabled rule matches), true for the excluded tab (the
    /// others, each with the first enabled rule that matches it).</summary>
    public bool Excluded { get; init; }

    /// <summary>True for the entries of both tabs, each with the rule that excludes it (null on the journal);
    /// <see cref="Excluded"/> is then ignored.</summary>
    public bool AllTabs { get; init; }

    /// <summary>Whether <paramref name="entry"/> passes the level, date and text filters (not the file nor the tab).</summary>
    internal bool Accepts(LogEntry entry) =>
        (MinLevel is not { } level || entry.Level >= level)
        && (From is not { } from || entry.Timestamp >= from)
        && (To is not { } to || entry.Timestamp <= to)
        && (Text is null || MatchesText(entry, Text));

    private static bool MatchesText(LogEntry entry, string text) =>
        Contains(entry.Message, text) || Contains(entry.Category, text) || Contains(entry.Exception, text) || Contains(entry.CorrelationId, text);

    private static bool Contains(string? field, string text) => field?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>What <see cref="ILogJournal.SearchAsync"/> found, and what it had to leave out.</summary>
public sealed record LogSearchResult
{
    /// <summary>The entries of the tab searched (or of both, <see cref="LogSearch.AllTabs"/>), newest first.</summary>
    public required IReadOnlyList<LogRow> Entries { get; init; }

    /// <summary>The entries of the journal tab under the filters, whatever tab was searched.</summary>
    public required int JournalCount { get; init; }

    /// <summary>The entries of the excluded tab under the filters, whatever tab was searched.</summary>
    public required int ExcludedCount { get; init; }

    /// <summary>The rules the search applied (the enabled ones exclude), in stored order.</summary>
    public required IReadOnlyList<LogRule> Rules { get; init; }

    /// <summary>Why the rules could not be read (then none applied), or null.</summary>
    public required string? RulesError { get; init; }

    /// <summary>True when <see cref="LogsOptions.MaxBytesRead"/> left older text unread.</summary>
    public required bool Truncated { get; init; }

    /// <summary>The files that had unreadable lines or could not be read.</summary>
    public required IReadOnlyList<LogFileIssue> Issues { get; init; }
}
