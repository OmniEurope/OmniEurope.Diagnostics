// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The CSV export of a selection (RFC 4180, UTF-8 with a byte order mark, every cell quoted). A cell starting with
/// <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is prefixed with a quote, so a spreadsheet never
/// runs a log message as a formula.
/// </summary>
internal static class LogsCsv
{
    /// <summary>The column names, invariant: the export is read by tools as much as by people.</summary>
    private static readonly string[] Header = ["Timestamp", "Level", "Category", "Message", "CorrelationId", "Exception", "File", "Line", "Rule"];

    /// <summary>The export of <paramref name="rows"/>, in their order.</summary>
    public static byte[] Write(IReadOnlyList<LogRow> rows)
    {
        var text = new StringBuilder();
        AppendLine(text, Header);
        foreach (var row in rows)
        {
            var entry = row.Entry;
            AppendLine(text, [
                entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                entry.Level.ToString(),
                entry.Category,
                entry.Message,
                entry.CorrelationId,
                entry.Exception,
                entry.File,
                entry.Line?.ToString(CultureInfo.InvariantCulture),
                row.Rule?.Pattern,
            ]);
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static void AppendLine(StringBuilder text, IEnumerable<string?> cells)
    {
        text.AppendJoin(',', cells.Select(Cell));
        text.Append("\r\n");
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
