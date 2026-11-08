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
/// left, so the budget always keeps the most recent events. Files are opened for reading only and shared for
/// writing and deleting, so the host's Serilog sink keeps writing, rolling and deleting them meanwhile. A last
/// line without its end of line is being written: it is left for the next read rather than counted unreadable.
/// </summary>
internal static class LogFileReader
{
    /// <summary>Reads <paramref name="files"/> (newest first) within <paramref name="maxBytes"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled: checked
    /// before each file and each line.</exception>
    public static LogReadResult Read(IReadOnlyList<FileInfo> files, long maxBytes, CancellationToken cancellationToken = default)
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
            remaining -= ReadFile(file, remaining, result, cancellationToken);
        }
        return result;
    }

    /// <summary>Opens <paramref name="path"/> shared for writing and deleting, so the host's sink is never blocked.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

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

    private static long ReadFile(FileInfo file, long budget, LogReadResult result, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = OpenShared(file.FullName);
            var length = stream.Length;
            var take = (int)Math.Min(length, budget);
            var fromEnd = length > take;
            if (fromEnd)
            {
                stream.Seek(length - take, SeekOrigin.Begin);
                result.Truncated = true;
            }
            var buffer = new byte[take];
            var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
            ParseLines(buffer.AsMemory(0, read), fromEnd, file.Name, result, cancellationToken);
            return read;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deleted or rolled away since the listing, or locked: the page names the file it could not read.
            result.Issues.Add(new LogFileIssue(file.Name) { Failed = true });
            return 0;
        }
    }

    private static void ParseLines(ReadOnlyMemory<byte> text, bool fromEnd, string file, LogReadResult result, CancellationToken cancellationToken)
    {
        var issue = new IssueBuilder(file);
        var lineNumber = 0;
        var start = SkipBom(text.Span);
        if (fromEnd)
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
            ParseLine(line, complete, fromEnd ? null : lineNumber, issue, result);
        }
        if (issue.UnreadableCount > 0)
            result.Issues.Add(new LogFileIssue(file) { UnreadableCount = issue.UnreadableCount, Lines = issue.Lines });
    }

    private static int SkipBom(ReadOnlySpan<byte> text) =>
        text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;

    /// <summary>The unreadable lines of one file, while it is read.</summary>
    private sealed class IssueBuilder(string file)
    {
        public string File { get; } = file;

        public int UnreadableCount { get; set; }

        public List<int> Lines { get; } = [];
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, bool complete, int? lineNumber, IssueBuilder issue, LogReadResult result)
    {
        var trimmed = line.TrimEnd((byte)'\r');
        if (trimmed.Span.Trim((byte)' ').IsEmpty)
            return;
        var entry = LogLineParser.Parse(trimmed);
        if (entry is not null)
        {
            result.Entries.Add(entry with { File = issue.File, Line = lineNumber, Sequence = result.Entries.Count });
            return;
        }
        if (!complete)
            return;
        issue.UnreadableCount++;
        if (lineNumber is { } number && issue.Lines.Count < LogFileIssue.LinesShown)
            issue.Lines.Add(number);
    }
}
