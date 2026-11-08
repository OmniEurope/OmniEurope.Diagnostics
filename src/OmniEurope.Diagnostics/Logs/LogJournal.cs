// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The default <see cref="ILogJournal"/>, over <see cref="LogDirectory"/>, <see cref="LogFileReader"/> and the
/// host's <see cref="ILogRuleStore"/>. The files are read synchronously within the call (as the pages always did),
/// the token checked before each file and each line; the rules are awaited from the store.
/// </summary>
internal sealed class LogJournal(LogDirectory directory, IOptions<LogsOptions> options, ILogRuleStore rules, TimeProvider time)
    : ILogJournal
{
    private readonly long _maxBytesRead = options.Value.MaxBytesRead;

    /// <inheritdoc />
    public Task<IReadOnlyList<LogFileInfo>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        SyncTask.Run(() => (IReadOnlyList<LogFileInfo>)directory.List().Select(Info).ToList(), cancellationToken);

    /// <inheritdoc />
    public async Task<LogSearchResult> SearchAsync(LogSearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var set = await rules.LoadAsync(cancellationToken);
        var read = LogFileReader.Read(FilesOf(search.File), _maxBytesRead, cancellationToken);
        var rows = LogSelection.Rows(read.Entries, set.Rules, search);
        var excludedCount = rows.Count(row => row.Rule is not null);
        return new LogSearchResult
        {
            Entries = search.AllTabs ? rows : rows.Where(row => (row.Rule is not null) == search.Excluded).ToList(),
            JournalCount = rows.Count - excludedCount,
            ExcludedCount = excludedCount,
            Rules = set.Rules,
            RulesError = set.Error,
            Truncated = read.Truncated,
            Issues = read.Issues,
        };
    }

    /// <inheritdoc />
    public Task<Stream?> OpenFileAsync(string name, CancellationToken cancellationToken = default) =>
        SyncTask.Run(() => Open(name), cancellationToken);

    /// <inheritdoc />
    public Task<LogPurgeResult> PurgeAsync(int olderThanDays, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(olderThanDays);
        return SyncTask.Run(() => directory.Purge(olderThanDays, time.GetUtcNow(), cancellationToken), cancellationToken);
    }

    private IReadOnlyList<FileInfo> FilesOf(string? name)
    {
        var files = directory.List();
        if (string.IsNullOrEmpty(name))
            return files;
        var (_, file) = LogDirectory.Find(name, files);
        return file is null ? [] : [file];
    }

    private Stream? Open(string name)
    {
        var (_, file) = LogDirectory.Find(name, directory.List());
        if (file is null)
            return null;
        try
        {
            return LogFileReader.OpenShared(file.FullName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted or rolled away since the listing: as if it were not listed. A locked file faults the task.
            return null;
        }
    }

    private static LogFileInfo Info(FileInfo file) =>
        new(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
}
