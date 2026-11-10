// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Reads one line of a plain-text log file (older files, a third-party writer): the whole line is the message, the
/// level is <see cref="LogSeverity.Information"/>. When the line starts with an ISO date (<c>2026-11-10</c>), followed
/// or not by a time and an offset (<c>2026-11-10 12:00:00.123 +01:00 [INF] ...</c>,
/// <c>2026-11-10T12:00:00Z ...</c>), that instant is the entry's timestamp; otherwise it is the one given (the
/// previous line's), so a stack trace keeps the instant of the line it follows.
/// </summary>
internal static class LogTextLine
{
    /// <summary>The most leading words tried as a timestamp: date, time and offset.</summary>
    private const int MaxTimestampWords = 3;

    /// <summary>The entry of <paramref name="line"/>, timestamped <paramref name="fallback"/> when it carries none.</summary>
    public static LogEntry Parse(string line, DateTimeOffset fallback) =>
        new(LeadingTimestamp(line) ?? fallback, LogSeverity.Information, line, null, null, null, null, null, []);

    /// <summary>The instant <paramref name="line"/> starts with, in UTC, or null when it starts with no ISO date.</summary>
    internal static DateTimeOffset? LeadingTimestamp(string line)
    {
        if (!StartsWithIsoDate(line))
            return null;
        var words = line.Split(' ', MaxTimestampWords + 1, StringSplitOptions.None);
        for (var count = Math.Min(MaxTimestampWords, words.Length); count > 0; count--)
        {
            var candidate = string.Join(' ', words, 0, count);
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                return timestamp;
        }
        return null;
    }

    private static bool StartsWithIsoDate(string line) =>
        line.Length >= 10
        && char.IsAsciiDigit(line[0]) && char.IsAsciiDigit(line[1]) && char.IsAsciiDigit(line[2]) && char.IsAsciiDigit(line[3])
        && line[4] == '-' && char.IsAsciiDigit(line[5]) && char.IsAsciiDigit(line[6])
        && line[7] == '-' && char.IsAsciiDigit(line[8]) && char.IsAsciiDigit(line[9]);
}
