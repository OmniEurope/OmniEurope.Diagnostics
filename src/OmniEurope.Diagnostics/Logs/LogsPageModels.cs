// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>The links every page shares, from the address the host mapped the pages on.</summary>
/// <param name="BasePath">The journal's path, the request's path base included (<c>/logs</c>).</param>
internal sealed record LogsLinks(string BasePath)
{
    /// <summary>The journal with <paramref name="query"/>.</summary>
    public string Journal(LogQuery query)
    {
        var href = BasePath + query.ToQueryString();
        return href.Length == 0 ? "/" : href;
    }

    /// <summary>The rules page.</summary>
    public string Rules => BasePath + "/rules";

    /// <summary>The purge form's target.</summary>
    public string Purge => BasePath + "/purge";

    /// <summary>The download of <paramref name="file"/>.</summary>
    public string Download(string file) => BasePath + "/download" + Microsoft.AspNetCore.Http.QueryString.Create("file", file).ToUriComponent();

    /// <summary>The rules page prefilled with a rule excluding entries like <paramref name="entry"/>.</summary>
    public string Exclude(LogEntry entry)
    {
        var pairs = new List<KeyValuePair<string, string?>> { KeyValuePair.Create("pattern", (string?)SuggestedPattern(entry)) };
        if (entry.Category is not null)
            pairs.Add(KeyValuePair.Create("category", (string?)entry.Category));
        return Rules + Microsoft.AspNetCore.Http.QueryString.Create(pairs).ToUriComponent();
    }

    /// <summary>
    /// The text a rule for <paramref name="entry"/> starts from: the template's literal text before its first hole,
    /// which every message of that template contains, else the start of the message; 60 characters at most.
    /// </summary>
    internal static string SuggestedPattern(LogEntry entry) => LogRulePattern.Suggest(entry);
}

/// <summary>What the journal and excluded tabs render.</summary>
internal sealed class LogsJournalModel
{
    /// <summary>The links of the pages.</summary>
    public required LogsLinks Links { get; init; }

    /// <summary>The request's tab, filters and modes.</summary>
    public required LogQuery Query { get; init; }

    /// <summary>The listed log files, newest first.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>The configured folder and pattern.</summary>
    public required string Source { get; init; }

    /// <summary>The configured folder.</summary>
    public required string DirectoryLabel { get; init; }

    /// <summary>The configured rules file.</summary>
    public required string RulesLabel { get; init; }

    /// <summary>When the files were read.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>Entries of the journal tab under the filters.</summary>
    public required int JournalCount { get; init; }

    /// <summary>Entries of the excluded tab under the filters.</summary>
    public required int ExcludedCount { get; init; }

    /// <summary>The number of rules.</summary>
    public required int RuleCount { get; init; }

    /// <summary>The page's rows.</summary>
    public required IReadOnlyList<LogRow> Rows { get; init; }

    /// <summary>The page shown, from 1.</summary>
    public required int Page { get; init; }

    /// <summary>The number of pages, at least 1.</summary>
    public required int PageCount { get; init; }

    /// <summary>The rows before this page.</summary>
    public required int Offset { get; init; }

    /// <summary>Files with unreadable lines, or that could not be read.</summary>
    public required IReadOnlyList<LogFileIssue> Issues { get; init; }

    /// <summary>True when the byte cap left older entries unread.</summary>
    public required bool Truncated { get; init; }

    /// <summary>The cap, in bytes.</summary>
    public required long MaxBytesRead { get; init; }

    /// <summary>Why the rules file could not be read, or null.</summary>
    public required string? RulesError { get; init; }

    /// <summary>The reload period of follow mode, in seconds.</summary>
    public required int FollowSeconds { get; init; }

    /// <summary>The entries of the current tab under the filters.</summary>
    public int Total => Query.Excluded ? ExcludedCount : JournalCount;
}

/// <summary>A rule and the entries it excludes from the files read.</summary>
/// <param name="Rule">The rule.</param>
/// <param name="Count">Entries it excludes: 0 while disabled.</param>
internal sealed record LogRuleCount(LogRule Rule, int Count);

/// <summary>What the rules page renders.</summary>
internal sealed class LogRulesModel
{
    /// <summary>The links of the pages.</summary>
    public required LogsLinks Links { get; init; }

    /// <summary>The configured rules file.</summary>
    public required string RulesLabel { get; init; }

    /// <summary>The antiforgery form field's name.</summary>
    public required string TokenField { get; init; }

    /// <summary>The antiforgery request token.</summary>
    public required string? Token { get; init; }

    /// <summary>The prefilled text, from <c>?pattern=</c>.</summary>
    public required string? Pattern { get; init; }

    /// <summary>The prefilled category, from <c>?category=</c>.</summary>
    public required string? Category { get; init; }

    /// <summary>How many entries the prefilled rule would exclude, when there is one.</summary>
    public required int? PrefillCount { get; init; }

    /// <summary>The rules and their counts.</summary>
    public required IReadOnlyList<LogRuleCount> Rules { get; init; }

    /// <summary>Why the rules file could not be read, or null.</summary>
    public required string? RulesError { get; init; }

    /// <summary>The purge form's days.</summary>
    public required int PurgeDays { get; init; }

    /// <summary>The files a purge of <see cref="PurgeDays"/> would delete.</summary>
    public required int PurgeCount { get; init; }

    /// <summary>Their size, in bytes.</summary>
    public required long PurgeBytes { get; init; }

    /// <summary>The change just saved (<c>added</c>, <c>enabled</c>, <c>disabled</c>, <c>deleted</c>), or null.</summary>
    public required string? Done { get; init; }

    /// <summary>The purge just run, or null.</summary>
    public required LogPurgeResult? Purged { get; init; }

    /// <summary>True when the byte cap left older entries out of the counts.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>The model of the current request, set by the endpoint and read by the page it renders.</summary>
internal sealed class LogsPageState
{
    /// <summary>The journal page's model.</summary>
    public LogsJournalModel? Journal { get; set; }

    /// <summary>The rules page's model.</summary>
    public LogRulesModel? Rules { get; set; }
}
