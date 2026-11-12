// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace OmniEurope.Diagnostics;

/// <summary>How the page writes durations and instants, in the current culture.</summary>
internal static class PerformanceFormat
{
    /// <summary>"12 ms" under a second, "1.20 s" from a second up.</summary>
    public static string Milliseconds(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " s"
            : Math.Round(value).ToString(CultureInfo.CurrentCulture) + " ms";

    /// <summary>A UTC instant, sortable and free of the server's time zone: "2026-10-06 14:05:09".</summary>
    public static string Utc(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
