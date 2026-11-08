// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.Extensions.Localization;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>How the logs pages write dates, counts and sizes.</summary>
internal static class LogsFormat
{
    /// <summary>The longest message preview a table row shows.</summary>
    public const int PreviewLength = 160;

    /// <summary>English for an English UI culture; otherwise French, the neutral resource fallback.</summary>
    public static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "fr";

    /// <summary>An instant as the page shows it, in UTC, with its milliseconds (format from the resources).</summary>
    public static string Date(DateTimeOffset date, IStringLocalizer strings) =>
        date.UtcDateTime.ToString(strings["DateFormat"], CultureInfo.InvariantCulture);

    /// <summary>An instant for the <c>datetime</c> attribute: ISO 8601, UTC.</summary>
    public static string Iso(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A count with the culture's digit grouping.</summary>
    public static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A size in kilobytes under a megabyte, in megabytes above, rounded up.</summary>
    public static string Size(long bytes, IStringLocalizer strings) => bytes < 1024 * 1024
        ? string.Format(CultureInfo.CurrentCulture, strings["SizeKilobytes"], Count((bytes + 1023) / 1024))
        : string.Format(CultureInfo.CurrentCulture, strings["SizeMegabytes"], Count((bytes + 1024 * 1024 - 1) / (1024 * 1024)));

    /// <summary>The first line of a message, cut to <see cref="PreviewLength"/> characters with an ellipsis.</summary>
    public static string Preview(string message)
    {
        var line = message.Split('\n')[0].TrimEnd('\r');
        return line.Length <= PreviewLength && line.Length == message.Length ? line : line[..Math.Min(line.Length, PreviewLength)].TrimEnd() + "…";
    }
}
