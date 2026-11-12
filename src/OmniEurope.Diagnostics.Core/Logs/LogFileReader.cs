// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>What one file could not give: lines that are not events, or the whole file when it cannot be opened.</summary>
/// <param name="file">The file's name.</param>
public sealed class LogFileIssue(string file)
{
    /// <summary>How many line numbers are kept for the notice.</summary>
    public const int LinesShown = 10;

    /// <summary>The file's name.</summary>
    public string File { get; } = file;

    /// <summary>True when the file could not be opened or read at all.</summary>
    public bool Failed { get; init; }

    /// <summary>The lines that are not readable events.</summary>
    public int UnreadableCount { get; init; }

    /// <summary>The first <see cref="LinesShown"/> of their numbers, when known (unknown when the file was read from
    /// its end).</summary>
    public IReadOnlyList<int> Lines { get; init; } = [];
}

/// <summary>The events one request read, newest file first, and what it had to leave out.</summary>
internal sealed class LogReadResult
{
    /// <summary>The events, in reading order.</summary>
    public List<LogEntry> Entries { get; } = [];

    /// <summary>The files that had unreadable lines or could not be read.</summary>
    public List<LogFileIssue> Issues { get; } = [];

    /// <summary>True when <see cref="LogsOptions.MaxBytesRead"/> left older text unread.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Reads log files within a byte budget: the newest file first, each from its end when it is larger than what is
/// left, so the budget always keeps the most recent events. Files are opened for asynchronous reading only and shared
/// for writing and deleting, so the host's sink keeps writing, rolling and deleting them meanwhile. A JSON file's last
/// line without its end of line is being written: it is left for the next read rather than counted unreadable. A
/// <c>.log</c> file is plain text (<see cref="LogTextLine"/>): every non-empty line is an entry.
/// </summary>
internal static class LogFileReader
{
    /// <summary>The extension of the files read as plain text, one entry per line.</summary>
    public const string TextExtension = ".log";

    /// <summary>Reads <paramref name="files"/> (newest first) within <paramref name="maxBytes"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled: checked
    /// before each file, during each read and before each line.</exception>
    public static async Task<LogReadResult> ReadAsync(IReadOnlyList<FileInfo> files, long maxBytes, CancellationToken cancellationToken = default)
    {
        var result = new LogReadResult();
        var remaining = maxBytes;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                result.Truncated = true;
                break;
            }
            remaining -= await ReadFileAsync(file, remaining, result, cancellationToken);
        }
        return result;
    }

    /// <summary>Opens <paramref name="path"/> shared for writing and deleting, so the host's sink is never blocked.</summary>
    /// <param name="path">The file.</param>
    /// <param name="asynchronous">True to open it for asynchronous reads.</param>
    internal static FileStream OpenShared(string path, bool asynchronous = false) => new(path, SharedOptions(asynchronous));

    /// <summary>The options <see cref="OpenShared"/> opens a file with; separate so they can be checked on every platform.</summary>
    /// <param name="asynchronous">True to open the file for asynchronous reads.</param>
    internal static FileStreamOptions SharedOptions(bool asynchronous) => new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 4096,
        Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None,
    };

    /// <summary>
    /// Copies the bytes <paramref name="source"/> holds when the copy starts, and no more: the host's sink keeps
    /// appending to a log file while it is downloaded (the download itself writes log lines), and a response that
    /// copied the growing file would overrun the length it announced. A file cut shorter meanwhile ends the copy early.
    /// </summary>
    internal static async Task CopySnapshotAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var remaining = source.Length - source.Position;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static async Task<long> ReadFileAsync(FileInfo file, long budget, LogReadResult result, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = OpenShared(file.FullName, asynchronous: true);
            var length = stream.Length;
            var take = (int)Math.Min(length, budget);
            var fromEnd = length > take;
            var startsOnLine = true;
            if (fromEnd)
            {
                // The byte just before the window tells whether it starts a line or cuts into one.
                stream.Seek(length - take - 1, SeekOrigin.Begin);
                var before = new byte[1];
                startsOnLine = await stream.ReadAsync(before, cancellationToken) == 1 && before[0] == '\n';
                result.Truncated = true;
            }
            var buffer = new byte[take];
            var read = await stream.ReadAtLeastAsync(buffer, take, throwOnEndOfStream: false, cancellationToken);
            var state = new FileState(file.Name, file.Name.EndsWith(TextExtension, StringComparison.OrdinalIgnoreCase),
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            ParseLines(buffer.AsMemory(0, read), fromEnd, startsOnLine, state, result, cancellationToken);
            return read;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deleted or rolled away since the listing, or locked: the page names the file it could not read.
            result.Issues.Add(new LogFileIssue(file.Name) { Failed = true });
            return 0;
        }
    }

    private static void ParseLines(ReadOnlyMemory<byte> text, bool fromEnd, bool startsOnLine, FileState file, LogReadResult result, CancellationToken cancellationToken)
    {
        var lineNumber = 0;
        var start = SkipBom(text.Span);
        if (fromEnd && !startsOnLine)
        {
            // The budget cut into a line: what precedes the first end of line is a fragment.
            var first = text.Span.IndexOf((byte)'\n');
            start = first < 0 ? text.Length : first + 1;
        }
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = text.Span[start..].IndexOf((byte)'\n');
            var complete = end >= 0;
            var line = text[start..(complete ? start + end : text.Length)];
            start = complete ? start + end + 1 : text.Length;
            lineNumber++;
            ParseLine(line, complete, fromEnd ? null : lineNumber, file, result);
        }
        if (file.UnreadableCount > 0)
            result.Issues.Add(new LogFileIssue(file.Name) { UnreadableCount = file.UnreadableCount, Lines = file.Lines });
    }

    private static int SkipBom(ReadOnlySpan<byte> text) =>
        text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;

    /// <summary>One file while it is read: its kind, the last timestamp a text line gave, its unreadable lines.</summary>
    private sealed class FileState(string name, bool text, DateTimeOffset lastWrite)
    {
        public string Name { get; } = name;

        public bool Text { get; } = text;

        /// <summary>The timestamp a text line without its own gets: the previous line's, at first the file's last write.</summary>
        public DateTimeOffset LastTimestamp { get; set; } = lastWrite;

        public int UnreadableCount { get; set; }

        public List<int> Lines { get; } = [];
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, bool complete, int? lineNumber, FileState file, LogReadResult result)
    {
        var trimmed = line.TrimEnd((byte)'\r');
        if (trimmed.Span.Trim((byte)' ').IsEmpty)
            return;
        var entry = file.Text ? TextEntry(trimmed.Span, file) : LogLineParser.Parse(trimmed);
        if (entry is not null)
        {
            result.Entries.Add(entry with { File = file.Name, Line = lineNumber, Sequence = result.Entries.Count });
            return;
        }
        if (!complete)
            return;
        file.UnreadableCount++;
        if (lineNumber is { } number && file.Lines.Count < LogFileIssue.LinesShown)
            file.Lines.Add(number);
    }

    private static LogEntry TextEntry(ReadOnlySpan<byte> line, FileState file)
    {
        var entry = LogTextLine.Parse(System.Text.Encoding.UTF8.GetString(line), file.LastTimestamp);
        file.LastTimestamp = entry.Timestamp;
        return entry;
    }
}
