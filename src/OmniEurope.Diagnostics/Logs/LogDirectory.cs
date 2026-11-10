// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>How a file name from a request matched the listing.</summary>
internal enum LogFileLookup
{
    /// <summary>No file was asked for.</summary>
    None,

    /// <summary>A listed log file.</summary>
    Found,

    /// <summary>Not a bare file name (a separator, a parent, a rooted path): refused before any lookup.</summary>
    Invalid,

    /// <summary>A bare name that is not in the listing.</summary>
    Missing,
}

/// <summary>What a purge deleted, and what it could not.</summary>
/// <param name="Deleted">Files deleted.</param>
/// <param name="Failed">Files that could not be deleted (on Windows: held open without delete sharing, or read-only;
/// POSIX systems delete both).</param>
public sealed record LogPurgeResult(int Deleted, int Failed);

/// <summary>
/// The log folder of <see cref="LogsOptions"/>: the only place the page lists, reads, downloads and deletes files.
/// A file a request names is never combined into a path: it must be a bare name, and the file used is the one the
/// listing found, so no request reaches outside the folder or a file the pattern does not match.
/// </summary>
internal sealed class LogDirectory
{
    private readonly LogsOptions _options;

    /// <summary>Resolves the folder against the host's content root.</summary>
    public LogDirectory(IOptions<LogsOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _options = options.Value;
        FullPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.Directory));
    }

    /// <summary>The folder's absolute path.</summary>
    public string FullPath { get; }

    /// <summary>The folder and pattern as configured, for the page's header.</summary>
    public string Source => Label(_options.FilePattern);

    /// <summary>The rules file as configured, for the rules page.</summary>
    public string RulesLabel => Label(_options.RulesFile);

    /// <summary>The configured folder, for the empty-folder notice.</summary>
    public string DirectoryLabel => _options.Directory.TrimEnd('/', '\\') + "/";

    /// <summary>The rules file's absolute path.</summary>
    public string RulesPath => Path.Combine(FullPath, _options.RulesFile);

    /// <summary>The rules file's name.</summary>
    public string RulesFile => _options.RulesFile;

    /// <summary>
    /// The log files: directly in the folder, matching the pattern, never the rules file nor its temporary copies;
    /// the most recently written first. An absent folder has none.
    /// </summary>
    public IReadOnlyList<FileInfo> List()
    {
        var folder = new DirectoryInfo(FullPath);
        if (!folder.Exists)
            return [];
        return Patterns(_options.FilePattern)
            .SelectMany(pattern => folder.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            .DistinctBy(file => file.Name, StringComparer.Ordinal)
            .Where(file => !file.Name.StartsWith(_options.RulesFile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The patterns of a <see cref="LogsOptions.FilePattern"/>: separated by <c>;</c>, blanks dropped.</summary>
    public static IReadOnlyList<string> Patterns(string? filePattern) =>
        (filePattern ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// A name without any path: no separator, no parent, no drive or root, no character a file name cannot hold.
    /// </summary>
    public static bool IsBareName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name);

    /// <summary>Looks <paramref name="name"/> up in <paramref name="files"/>: the listed file, or why there is none.</summary>
    public static (LogFileLookup Lookup, FileInfo? File) Find(string? name, IReadOnlyList<FileInfo> files) =>
        Find(name, files, file => file.Name);

    /// <summary>Looks <paramref name="name"/> up in <paramref name="files"/>: the listed file, or why there is none.</summary>
    public static (LogFileLookup Lookup, LogFileInfo? File) Find(string? name, IReadOnlyList<LogFileInfo> files) =>
        Find(name, files, file => file.Name);

    private static (LogFileLookup Lookup, T? File) Find<T>(string? name, IReadOnlyList<T> files, Func<T, string> nameOf)
        where T : class
    {
        if (string.IsNullOrEmpty(name))
            return (LogFileLookup.None, null);
        if (!IsBareName(name))
            return (LogFileLookup.Invalid, null);
        // The exact name first: a case-sensitive file system may hold both app.json and App.json.
        var file = files.FirstOrDefault(listed => string.Equals(nameOf(listed), name, StringComparison.Ordinal))
            ?? files.FirstOrDefault(listed => string.Equals(nameOf(listed), name, StringComparison.OrdinalIgnoreCase));
        return file is null ? (LogFileLookup.Missing, null) : (LogFileLookup.Found, file);
    }

    /// <summary>Whether a file last written at <paramref name="lastWriteUtc"/> is more than <paramref name="days"/>
    /// days older than <paramref name="now"/>: what a purge deletes. Any number of days is accepted: more days than
    /// the calendar holds keeps every file.</summary>
    public static bool IsOlderThan(DateTime lastWriteUtc, int days, DateTimeOffset now) =>
        (now.UtcDateTime - DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)).TotalDays > days;

    /// <summary>The log files last written more than <paramref name="days"/> days before <paramref name="now"/>.</summary>
    public IReadOnlyList<FileInfo> OlderThan(int days, DateTimeOffset now) =>
        List().Where(file => IsOlderThan(file.LastWriteTimeUtc, days, now)).ToList();

    /// <summary>Deletes the log files of <see cref="OlderThan"/>; the rules file and other files are never touched.</summary>
    public LogPurgeResult Purge(int days, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        int deleted = 0, failed = 0;
        foreach (var file in OlderThan(days, now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDelete(file))
                deleted++;
            else
                failed++;
        }
        return new LogPurgeResult(deleted, failed);
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held open without delete sharing, or read-only: counted, and the page says how many remain.
            return false;
        }
    }

    private string Label(string file) => DirectoryLabel + file;
}
