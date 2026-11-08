// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>Serilog's levels, from the most verbose to the most severe; a filter keeps a level and above.</summary>
internal enum LogSeverity
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

/// <summary>One log event read from a file: what the page lists, filters, details and exports.</summary>
/// <param name="Timestamp">When the event happened, in UTC.</param>
/// <param name="Level">Its level; an event without one is <see cref="LogSeverity.Information"/>.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Template">The message template, when the file carries one.</param>
/// <param name="Category">The <c>SourceContext</c> property, when present.</param>
/// <param name="Exception">The exception text, when present.</param>
/// <param name="CorrelationId">The first of <c>CorrelationId</c>, <c>RequestId</c>, <c>TraceId</c> and the trace id.</param>
/// <param name="Request">The request method and path, when present.</param>
/// <param name="Properties">The other properties, in file order, their values as text.</param>
internal sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Level,
    string Message,
    string? Template,
    string? Category,
    string? Exception,
    string? CorrelationId,
    string? Request,
    IReadOnlyList<KeyValuePair<string, string>> Properties)
{
    /// <summary>The file the event was read from.</summary>
    public string File { get; init; } = "";

    /// <summary>Its line number in that file, unknown when the file was read from its end.</summary>
    public int? Line { get; init; }

    /// <summary>The order the event was read in, used to keep events of the same instant in file order.</summary>
    public long Sequence { get; init; }
}

/// <summary>An entry as a tab lists it: the rule that excludes it on the excluded tab, none on the journal.</summary>
internal sealed record LogRow(LogEntry Entry, LogRule? Rule);
