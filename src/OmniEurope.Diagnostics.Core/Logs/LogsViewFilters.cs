// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>The filter inputs of the logs view, as typed, and the search they ask for.</summary>
internal sealed class LogsViewFilters
{
    private static readonly string[] DateFormats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];

    /// <summary>The selected file name; empty for every file.</summary>
    public string File { get; set; } = "";

    /// <summary>The selected minimum level name; empty for every level.</summary>
    public string Level { get; set; } = "";

    /// <summary>The earliest instant, a <c>datetime-local</c> value read as UTC; empty for none.</summary>
    public string From { get; set; } = "";

    /// <summary>The latest instant, a <c>datetime-local</c> value read as UTC; empty for none.</summary>
    public string To { get; set; } = "";

    /// <summary>The searched text; blank for none.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// The search these inputs ask for, over both tabs (the view splits them itself). False, with a search
    /// that has no filter, when a date input holds text that is not a date.
    /// </summary>
    public bool TryToSearch(out LogSearch search)
    {
        search = new LogSearch { AllTabs = true };
        if (!TryDate(From, out var from) || !TryDate(To, out var to))
            return false;
        search = new LogSearch
        {
            File = Blank(File),
            MinLevel = Enum.TryParse<LogSeverity>(Level, out var level) && Enum.IsDefined(level) ? level : null,
            From = from,
            To = to,
            Text = Blank(Text),
            AllTabs = true,
        };
        return true;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TryDate(string text, out DateTimeOffset? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateTimeOffset.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        date = parsed;
        return true;
    }
}
