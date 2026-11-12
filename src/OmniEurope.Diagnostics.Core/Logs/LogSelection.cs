// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>Selects read entries for the journal and the excluded tab, newest first.</summary>
internal static class LogSelection
{
    /// <summary>The entries <paramref name="search"/> accepts, newest first, each with the first enabled rule that
    /// matches it, or none.</summary>
    public static List<LogRow> Rows(IEnumerable<LogEntry> entries, IReadOnlyList<LogRule> rules, LogSearch search)
    {
        var enabled = rules.Where(rule => rule.Enabled).ToList();
        return entries.OrderByDescending(entry => entry.Timestamp).ThenByDescending(entry => entry.Sequence)
            .Where(search.Accepts)
            .Select(entry => new LogRow(entry, enabled.FirstOrDefault(candidate => candidate.Matches(entry))))
            .ToList();
    }
}
