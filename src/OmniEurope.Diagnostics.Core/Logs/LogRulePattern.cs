// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>The text a new exclusion rule starts from.</summary>
internal static class LogRulePattern
{
    /// <summary>The longest suggested text.</summary>
    private const int SuggestedLength = 60;

    /// <summary>
    /// The text a rule for <paramref name="entry"/> starts from: the template's literal text before its first hole,
    /// which every message of that template contains, else the start of the message; 60 characters at most.
    /// </summary>
    public static string Suggest(LogEntry entry)
    {
        var literal = entry.Template is { } template ? template.Split('{')[0].Trim() : "";
        var text = literal.Length > 0 ? literal : entry.Message.Split('\n')[0].Trim();
        return text.Length <= SuggestedLength ? text : text[..SuggestedLength].TrimEnd();
    }
}
