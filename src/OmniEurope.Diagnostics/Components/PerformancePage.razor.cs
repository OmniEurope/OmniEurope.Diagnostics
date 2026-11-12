// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace OmniEurope.Diagnostics.Components;

/// <summary>A complete, unstyled HTML document of the host's request and operation performance, around
/// <see cref="OmniPerformanceView"/>.</summary>
public partial class PerformancePage
{
    [Inject] private IStringLocalizer<PerformanceStrings> L { get; set; } = default!;

    /// <summary>English for an English UI culture; otherwise French, the neutral resource fallback.</summary>
    internal static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "fr";
}
