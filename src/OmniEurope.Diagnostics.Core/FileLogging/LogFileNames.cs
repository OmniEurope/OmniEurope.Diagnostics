// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>
/// The names of the file logger's files: <c>{prefix}[-{instance}]-yyyyMMdd.json</c>, then <c>.1.json</c>,
/// <c>.2.json</c>... for the same day, and the recognition of that family when old files are deleted.
/// </summary>
internal sealed class LogFileNames
{
    private readonly string _stem;
    private readonly Regex _family;

    public LogFileNames(string prefix, string? instance)
    {
        Prefix = prefix;
        var sanitized = SanitizeInstance(instance);
        _stem = sanitized is null ? prefix : prefix + "-" + sanitized;
        // Every instance's files of this prefix, never a file that merely starts with the same letters.
        _family = new Regex("^" + Regex.Escape(prefix) + @"(-[A-Za-z0-9_.-]+)?-(?<day>\d{8})(\.\d+)?\.json$",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
    }

    /// <summary>The file prefix, also the start of the pattern listing the family.</summary>
    public string Prefix { get; }

    /// <summary>The name of the <paramref name="index"/>th file of <paramref name="day"/>, the first one being 0.</summary>
    public string For(DateOnly day, int index) =>
        _stem + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        + (index == 0 ? "" : "." + index.ToString(CultureInfo.InvariantCulture)) + ".json";

    /// <summary>The day of a file of the family, false for any other file.</summary>
    public bool TryGetDay(string fileName, out DateOnly day)
    {
        day = default;
        var match = _family.Match(fileName);
        return match.Success
            && DateOnly.TryParseExact(match.Groups["day"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>A prefix made of file-name-safe characters only, not starting with a dot.</summary>
    public static bool IsSafePrefix(string? prefix) =>
        !string.IsNullOrEmpty(prefix) && prefix.Length <= FileLoggingOptions.MaxNameLength && prefix[0] != '.' && prefix.All(IsSafe);

    /// <summary>The instance as a host-name-like token, or null when nothing usable is left.</summary>
    public static string? SanitizeInstance(string? instance)
    {
        if (instance is null)
            return null;
        var text = new string(instance.Select(c => IsSafe(c) ? c : '-').ToArray()).Trim('-', '.');
        if (text.Length > FileLoggingOptions.MaxNameLength)
            text = text[..FileLoggingOptions.MaxNameLength].TrimEnd('-', '.');
        return text.Length == 0 ? null : text;
    }

    private static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
