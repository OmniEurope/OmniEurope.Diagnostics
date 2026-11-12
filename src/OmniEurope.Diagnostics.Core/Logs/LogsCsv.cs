// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The CSV export of a selection (RFC 4180, UTF-8 with a byte order mark, every cell quoted). A cell starting with
/// <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is prefixed with a quote, so a spreadsheet never
/// runs a log message as a formula. The export is written straight to the response, never built in memory first.
/// </summary>
internal static class LogsCsv
{
    /// <summary>The column names, invariant: the export is read by tools as much as by people.</summary>
    private static readonly string[] Header = ["Timestamp", "Level", "Category", "Message", "CorrelationId", "Exception", "File", "Line", "Rule"];

    /// <summary>UTF-8 with a byte order mark, so a spreadsheet reads accents right.</summary>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Writes the export of <paramref name="rows"/>, in their order, to <paramref name="destination"/>, left open.</summary>
    public static async Task WriteAsync(IReadOnlyList<LogRow> rows, Stream destination, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(destination, Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: true);
        await WriteLineAsync(writer, Header, cancellationToken);
        foreach (var row in rows)
        {
            var entry = row.Entry;
            await WriteLineAsync(writer, [
                entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                entry.Level.ToString(),
                entry.Category,
                entry.Message,
                entry.CorrelationId,
                entry.Exception,
                entry.File,
                entry.Line?.ToString(CultureInfo.InvariantCulture),
                row.Rule?.Pattern,
            ], cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteLineAsync(StreamWriter writer, IEnumerable<string?> cells, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(string.Join(',', cells.Select(Cell)).AsMemory(), cancellationToken);
        await writer.WriteAsync("\r\n".AsMemory(), cancellationToken);
    }

    /// <summary>One quoted cell, its quotes doubled, a formula trigger neutralised.</summary>
    internal static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
