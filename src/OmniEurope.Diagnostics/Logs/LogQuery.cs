// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The journal's query string: the tab, the filters, the page and the modes. An unreadable level, date or page is
/// ignored rather than refused, so a hand-edited address still shows the page.
/// </summary>
internal sealed record LogQuery
{
    private const string LocalFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";
    private const string MinuteFormat = "yyyy-MM-ddTHH:mm";
    private static readonly string[] DateFormats = [MinuteFormat, "yyyy-MM-ddTHH:mm:ss", LocalFormat];

    /// <summary>The one file read, or null for every file.</summary>
    public string? File { get; init; }

    /// <summary>The lowest level kept, or null for every level.</summary>
    public LogSeverity? MinLevel { get; init; }

    /// <summary>The earliest instant kept (UTC), or null.</summary>
    public DateTimeOffset? FromDate { get; init; }

    /// <summary>The latest instant kept (UTC), or null.</summary>
    public DateTimeOffset? ToDate { get; init; }

    /// <summary>The text searched in the message, the category, the exception and the correlation id.</summary>
    public string? Text { get; init; }

    /// <summary>The page, from 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>True on the excluded tab.</summary>
    public bool Excluded { get; init; }

    /// <summary>True in follow mode: the page reloads itself.</summary>
    public bool Follow { get; init; }

    /// <summary>True for the CSV export of the selection.</summary>
    public bool Csv { get; init; }

    /// <summary><see cref="FromDate"/> as a <c>datetime-local</c> value, for the form and the links.</summary>
    public string? From => Local(FromDate);

    /// <summary><see cref="ToDate"/> as a <c>datetime-local</c> value, for the form and the links.</summary>
    public string? To => Local(ToDate);

    /// <summary>Reads the query string of a journal request.</summary>
    public static LogQuery Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new LogQuery
        {
            File = Value(query, "file"),
            MinLevel = ParseLevel(Value(query, "level")),
            FromDate = ParseDate(Value(query, "from")),
            ToDate = ParseDate(Value(query, "to")),
            Text = Value(query, "q"),
            Page = int.TryParse(Value(query, "page"), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1,
            Excluded = Value(query, "tab") == "excluded",
            Follow = Value(query, "follow") == "1",
            Csv = Value(query, "format") == "csv",
        };
    }

    /// <summary>A Serilog level name, or null when it is none.</summary>
    internal static LogSeverity? ParseLevel(string? text) =>
        Enum.TryParse<LogSeverity>(text, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : null;

    /// <summary>A <c>datetime-local</c> value, read as UTC; null when it is not one.</summary>
    internal static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private static string? Local(DateTimeOffset? date) =>
        date?.ToString(date.Value.Ticks % TimeSpan.TicksPerMinute == 0 ? MinuteFormat : LocalFormat, CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="entry"/> passes the level, date and text filters.</summary>
    public bool Accepts(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (MinLevel is not { } level || entry.Level >= level)
            && (FromDate is not { } from || entry.Timestamp >= from)
            && (ToDate is not { } to || entry.Timestamp <= to)
            && (Text is null || MatchesText(entry, Text));
    }

    private static bool MatchesText(LogEntry entry, string text) =>
        Contains(entry.Message, text) || Contains(entry.Category, text) || Contains(entry.Exception, text) || Contains(entry.CorrelationId, text);

    private static bool Contains(string? field, string text) => field?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The query string of this query (leading <c>?</c>, or empty), filters first.</summary>
    public string ToQueryString()
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        AddPair(pairs, "tab", Excluded ? "excluded" : null);
        AddPair(pairs, "file", File);
        AddPair(pairs, "level", MinLevel?.ToString());
        AddPair(pairs, "from", From);
        AddPair(pairs, "to", To);
        AddPair(pairs, "q", Text);
        AddPair(pairs, "page", Page > 1 ? Page.ToString(CultureInfo.InvariantCulture) : null);
        AddPair(pairs, "follow", Follow ? "1" : null);
        AddPair(pairs, "format", Csv ? "csv" : null);
        return QueryString.Create(pairs).ToUriComponent();
    }

    /// <summary>The same filters without the page and modes: what a tab link, the export or the form keeps.</summary>
    public LogQuery Filters() => this with { Page = 1, Follow = false, Csv = false };

    private static void AddPair(List<KeyValuePair<string, string?>> pairs, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            pairs.Add(KeyValuePair.Create(name, (string?)value));
    }

    private static string? Value(IQueryCollection query, string name)
    {
        var value = query[name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }
}
