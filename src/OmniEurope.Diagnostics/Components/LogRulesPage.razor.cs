// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Components;

/// <summary>
/// The exclusion rules of the logs pages and the purge of old files, a complete unstyled HTML document. Rendered by
/// the endpoint <c>MapOmniLogs</c> maps, which hands the page its model and its antiforgery token.
/// </summary>
public partial class LogRulesPage
{
    [Inject] private LogsPageState State { get; set; } = default!;
    [Inject] private IStringLocalizer<LogsStrings> L { get; set; } = default!;

    private LogRulesModel Model => State.Rules!;

    private LogsLinks Links => Model.Links;

    /// <summary>The confirmation of the change just saved; none for an unknown one.</summary>
    private string? DoneText => Model.Done is { } done && L["Done_" + done] is { ResourceNotFound: false } text ? text.Value : null;

    private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, L[key], values);
}
