// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace OmniEurope.Diagnostics.Components;

/// <summary>
/// The figures of <see cref="RequestPerformanceRecorder"/> as an embeddable, unstyled fragment: the window's slowest
/// calls and every route's percentiles, an operation showing its outcome instead of an HTTP method and status. The
/// host places it in its own page, under its own heading and authorization; it needs the recorder
/// (<c>AddOmniPerformanceCollector</c>) and localization (<c>AddLocalization</c>). Its section headings are
/// <c>h2</c>. A refresh button reads the figures again in an interactive render mode or a Blazor Hybrid web view;
/// static rendering shows no button.
/// </summary>
public partial class OmniPerformanceView
{
    /// <summary>Calls below which the nearest-rank 95th percentile is the maximum itself (ceil(0.95 n) = n).</summary>
    internal const int P95DistinctFromMaxCalls = 20;

    /// <summary>Calls below which the nearest-rank 99th percentile is the maximum itself (ceil(0.99 n) = n).</summary>
    internal const int P99DistinctFromMaxCalls = 100;

    private RequestPerformanceSummary _summary = default!;

    /// <summary>True when at least one route has too few calls for its 99th percentile to differ from its maximum,
    /// so the view explains the figures it writes in small print.</summary>
    private bool AnyPercentileRepeatsMax => _summary.Routes.Any(route => route.Count < P99DistinctFromMaxCalls);

    [Inject] private RequestPerformanceRecorder Recorder { get; set; } = default!;
    [Inject] private IStringLocalizer<PerformanceStrings> L { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() => _summary = Recorder.Summarize();

    /// <summary>Reads the figures again.</summary>
    private void Refresh() => _summary = Recorder.Summarize();

    /// <summary>The HTTP method of a request, or the word for an operation.</summary>
    private string MethodText(bool isOperation, string method) => isOperation ? L["Operation"] : method;

    /// <summary>The status code of a request, or the outcome of an operation.</summary>
    private string StatusText(RequestTimingSample sample)
    {
        if (!sample.IsOperation) return sample.StatusCode.ToString(CultureInfo.InvariantCulture);
        return sample.Succeeded ? L["Succeeded"] : L["Failed"];
    }
}
