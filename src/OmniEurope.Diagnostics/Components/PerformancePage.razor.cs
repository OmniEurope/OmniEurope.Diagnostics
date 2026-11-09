// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace OmniEurope.Diagnostics.Components;

/// <summary>A complete, unstyled HTML document of the host's request performance.</summary>
public partial class PerformancePage
{
    /// <summary>Calls below which the nearest-rank 95th percentile is the maximum itself (ceil(0.95 n) = n).</summary>
    internal const int P95DistinctFromMaxCalls = 20;

    /// <summary>Calls below which the nearest-rank 99th percentile is the maximum itself (ceil(0.99 n) = n).</summary>
    internal const int P99DistinctFromMaxCalls = 100;

    private RequestPerformanceSummary _summary = default!;

    /// <summary>True when at least one route has too few calls for its 99th percentile to differ from its maximum,
    /// so the page explains the figures it writes in small print.</summary>
    private bool AnyPercentileRepeatsMax => _summary.Routes.Any(route => route.Count < P99DistinctFromMaxCalls);

    [Inject] private RequestPerformanceRecorder Recorder { get; set; } = default!;
    [Inject] private IStringLocalizer<PerformanceStrings> L { get; set; } = default!;

    /// <summary>English for an English UI culture; otherwise French, the neutral resource fallback.</summary>
    internal static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "fr";

    /// <inheritdoc />
    protected override void OnInitialized() => _summary = Recorder.Summarize();
}
