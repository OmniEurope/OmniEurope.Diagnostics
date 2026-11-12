// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Reads the lines of a plain-text log file (older files, a third-party writer). A line that starts with a timestamp
/// opens an entry: an ISO date, followed or not by a time and an offset (<c>2026-11-10 12:00:00.123 +01:00 ...</c>,
/// <c>2026-11-10T12:00:00Z ...</c>), bare or between brackets (<c>[2026-11-10 12:00:00.123 +01:00] ...</c>). After the
/// timestamp, a level name (Serilog's <c>Information</c> or <c>INF</c>, Microsoft's <c>Trace</c> or <c>Critical</c>,
/// bare or between brackets) sets the entry's level, and a word ending with a colon right after it
/// (<c>My.Service:</c>) is its category; the rest of the line is the message. Without a level the whole text after the
/// timestamp is the message, level Information. A line that starts with no timestamp continues the entry before it
/// (a stack trace, a detail line); <see cref="LogFileReader"/> appends it to that entry's message. A line made only
/// of dashes or equal signs separates entries and is no entry itself.
/// </summary>
internal static class LogTextLine
{
    /// <summary>The most leading words tried as a timestamp: date, time and offset.</summary>
    private const int MaxTimestampWords = 3;

    /// <summary>The shortest run of dashes or equal signs read as a separator line.</summary>
    private const int MinSeparatorLength = 3;

    private static readonly Dictionary<string, LogSeverity> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Verbose"] = LogSeverity.Verbose, ["Trace"] = LogSeverity.Verbose, ["VRB"] = LogSeverity.Verbose, ["TRC"] = LogSeverity.Verbose,
        ["Debug"] = LogSeverity.Debug, ["DBG"] = LogSeverity.Debug,
        ["Information"] = LogSeverity.Information, ["INF"] = LogSeverity.Information,
        ["Warning"] = LogSeverity.Warning, ["WRN"] = LogSeverity.Warning,
        ["Error"] = LogSeverity.Error, ["ERR"] = LogSeverity.Error,
        ["Fatal"] = LogSeverity.Fatal, ["Critical"] = LogSeverity.Fatal, ["FTL"] = LogSeverity.Fatal, ["CRT"] = LogSeverity.Fatal,
    };

    /// <summary>
    /// The entry <paramref name="line"/> opens, or null when it starts with no timestamp and so continues the entry
    /// before it.
    /// </summary>
    public static LogEntry? ParseHeader(string line)
    {
        if (LeadingTimestamp(line, out var rest) is not { } timestamp)
            return null;
        var (level, category, message) = Describe(rest);
        return new LogEntry(timestamp, level, message.Length == 0 ? line : message, null, category, null, null, null, []);
    }

    /// <summary>A line that starts with no timestamp, read on its own: no entry before it in the text read.</summary>
    public static LogEntry Standalone(string line, DateTimeOffset fallback) =>
        new(fallback, LogSeverity.Information, line, null, null, null, null, null, []);

    /// <summary>True for a line made only of dashes or equal signs (spaces around them aside).</summary>
    public static bool IsSeparator(string line)
    {
        var trimmed = line.AsSpan().Trim();
        return trimmed.Length >= MinSeparatorLength && (!trimmed.ContainsAnyExcept('-') || !trimmed.ContainsAnyExcept('='));
    }

    /// <summary>The instant <paramref name="line"/> starts with, in UTC, or null when it starts with no ISO date.</summary>
    internal static DateTimeOffset? LeadingTimestamp(string line) => LeadingTimestamp(line, out _);

    private static DateTimeOffset? LeadingTimestamp(string line, out string rest)
    {
        rest = "";
        if (line.StartsWith('['))
        {
            var close = line.IndexOf(']');
            if (close < 0 || !StartsWithIsoDate(line.AsSpan(1)))
                return null;
            if (!TryParse(line[1..close], out var bracketed))
                return null;
            rest = line[(close + 1)..].TrimStart();
            return bracketed;
        }
        if (!StartsWithIsoDate(line))
            return null;
        var words = line.Split(' ', MaxTimestampWords + 1, StringSplitOptions.None);
        for (var count = Math.Min(MaxTimestampWords, words.Length); count > 0; count--)
        {
            if (TryParse(string.Join(' ', words, 0, count), out var timestamp))
            {
                rest = count < words.Length ? string.Join(' ', words, count, words.Length - count).TrimStart() : "";
                return timestamp;
            }
        }
        return null;
    }

    private static bool TryParse(string text, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    /// <summary>The level, the category and the message the text after a timestamp holds.</summary>
    private static (LogSeverity Level, string? Category, string Message) Describe(string rest)
    {
        var (word, afterWord) = FirstWord(rest);
        if (!Levels.TryGetValue(word.Trim('[', ']'), out var level))
            return (LogSeverity.Information, null, rest);
        var (next, afterNext) = FirstWord(afterWord);
        return next.Length > 1 && next.EndsWith(':') && next.IndexOf(':') == next.Length - 1
            ? (level, next[..^1], afterNext)
            : (level, null, afterWord);
    }

    private static (string Word, string After) FirstWord(string text)
    {
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].TrimStart());
    }

    private static bool StartsWithIsoDate(ReadOnlySpan<char> line) =>
        line.Length >= 10
        && char.IsAsciiDigit(line[0]) && char.IsAsciiDigit(line[1]) && char.IsAsciiDigit(line[2]) && char.IsAsciiDigit(line[3])
        && line[4] == '-' && char.IsAsciiDigit(line[5]) && char.IsAsciiDigit(line[6])
        && line[7] == '-' && char.IsAsciiDigit(line[8]) && char.IsAsciiDigit(line[9]);
}
