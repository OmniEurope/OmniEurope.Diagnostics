// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Components;

/// <summary>
/// The journal of the host's Serilog files, a complete unstyled HTML document: the journal and excluded tabs, the
/// filters, one page of entries newest first and their details. Rendered by the endpoint <c>MapOmniLogs</c> maps,
/// which reads the files and hands the page its model.
/// </summary>
public partial class LogsPage
{
    [Inject] private LogsPageState State { get; set; } = default!;
    [Inject] private IStringLocalizer<LogsStrings> L { get; set; } = default!;

    private LogsJournalModel Model => State.Journal!;

    private LogsLinks Links => Model.Links;

    private LogQuery Filters => Model.Query.Filters();

    private string Tab(string key, int count) => Format(key, LogsFormat.Count(count));

    private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, L[key], values);

    /// <summary>The detail's id: unique over every page, so a link to it stays valid while the page changes.</summary>
    private string Anchor(int index) =>
        (Model.Query.Excluded ? "excluded-" : "entry-") + (Model.Offset + index + 1).ToString(CultureInfo.InvariantCulture);

    private string IssueText(LogFileIssue issue)
    {
        if (issue.Failed)
            return L["FileFailed"];
        var text = issue.UnreadableCount == 1 ? L["FileUnreadableOne"].Value : Format("FileUnreadable", LogsFormat.Count(issue.UnreadableCount));
        return issue.Lines.Count == 0
            ? text + "."
            : text + " " + Format("UnreadableLines", string.Join(", ", issue.Lines.Select(line => LogsFormat.Count(line)))) + ".";
    }
}
