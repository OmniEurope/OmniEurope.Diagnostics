// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Renders a Serilog message template from its properties, for the files that carry the template alone (the
/// compact formatter writes <c>@mt</c> without <c>@m</c>). Deliberately simple: <c>{Name}</c>,
/// <c>{@Name}</c>, <c>{$Name}</c> and <c>{0}</c> take the property's value, a string without quotes and any
/// other value as its JSON text; <c>{Name:format}</c> formats a number or a date with the invariant culture;
/// an alignment (<c>{Name,10}</c>) is ignored; <c>{{</c> and <c>}}</c> are literal braces; a hole without a
/// property stays as written.
/// </summary>
internal static class MessageTemplateRenderer
{
    /// <summary>Substitutes every hole of <paramref name="template"/> with its property.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var text = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
            index = Append(template, index, properties, text);
        return text.ToString();
    }

    /// <summary>Appends what starts at <paramref name="index"/>: an escaped brace, a hole or one plain character.</summary>
    private static int Append(string template, int index, IReadOnlyDictionary<string, JsonElement> properties, StringBuilder text)
    {
        var current = template[index];
        if (IsEscaped(template, index, current))
        {
            text.Append(current);
            return index + 2;
        }
        var close = current == '{' ? template.IndexOf('}', index + 1) : -1;
        if (close < 0)
        {
            text.Append(current);
            return index + 1;
        }
        text.Append(Hole(template[(index + 1)..close], properties));
        return close + 1;
    }

    private static bool IsEscaped(string template, int index, char current) =>
        current is '{' or '}' && index + 1 < template.Length && template[index + 1] == current;

    /// <summary>The text of one hole: the formatted property, or the hole itself when there is none.</summary>
    private static string Hole(string token, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var body = token.TrimStart('@', '$');
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        var format = colon < 0 ? null : body[(colon + 1)..];
        var name = (colon < 0 ? body : body[..colon]).Split(',')[0];
        return properties.TryGetValue(name, out var value) ? Format(value, format) : "{" + token + "}";
    }

    /// <summary>A property value as text: <paramref name="format"/> applies to numbers and dates only.</summary>
    internal static string Format(JsonElement value, string? format)
    {
        if (format is not null && TryFormat(value, format, out var formatted))
            return formatted;
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static bool TryFormat(JsonElement value, string format, out string formatted)
    {
        try
        {
            formatted = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var whole) => whole.ToString(format, CultureInfo.InvariantCulture),
                JsonValueKind.Number => value.GetDouble().ToString(format, CultureInfo.InvariantCulture),
                JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                    => date.ToString(format, CultureInfo.InvariantCulture),
                _ => "",
            };
            return formatted.Length > 0;
        }
        catch (FormatException)
        {
            // A format the value's type does not know (D on a decimal number): the value is shown unformatted.
            formatted = "";
            return false;
        }
    }
}
