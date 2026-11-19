// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>Selects read entries for the journal and the excluded tab, in the order of <see cref="LogSearch.SortBy"/>.</summary>
internal static class LogSelection
{
    /// <summary>The entries <paramref name="search"/> accepts, in its order (newest first by default), each with the
    /// first enabled rule that matches it, or none.</summary>
    public static List<LogRow> Rows(IEnumerable<LogEntry> entries, IReadOnlyList<LogRule> rules, LogSearch search)
    {
        var enabled = rules.Where(rule => rule.Enabled).ToList();
        return Order(entries.Where(search.Accepts), search)
            .Select(entry => new LogRow(entry, enabled.FirstOrDefault(candidate => candidate.Matches(entry))))
            .ToList();
    }

    private static IOrderedEnumerable<LogEntry> Order(IEnumerable<LogEntry> entries, LogSearch search)
    {
        var descending = search.Descending;
        return search.SortBy switch
        {
            LogSortField.Level => Primary(entries, entry => entry.Level, descending),
            LogSortField.Category => Primary(entries, entry => entry.Category ?? "", descending, StringComparer.OrdinalIgnoreCase),
            _ => entries.OrderBy(entry => entry.Timestamp, descending).ThenBy(entry => entry.Sequence, descending),
        };
    }

    // Level and category ties are always newest first, whatever the direction, so the page stays readable.
    private static IOrderedEnumerable<LogEntry> Primary<TKey>(
        IEnumerable<LogEntry> entries, Func<LogEntry, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        entries.OrderBy(key, descending, comparer)
            .ThenByDescending(entry => entry.Timestamp).ThenByDescending(entry => entry.Sequence);

    private static IOrderedEnumerable<T> OrderBy<T, TKey>(
        this IEnumerable<T> source, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    private static IOrderedEnumerable<T> ThenBy<T, TKey>(
        this IOrderedEnumerable<T> source, Func<T, TKey> key, bool descending) =>
        descending ? source.ThenByDescending(key) : source.ThenBy(key);
}
