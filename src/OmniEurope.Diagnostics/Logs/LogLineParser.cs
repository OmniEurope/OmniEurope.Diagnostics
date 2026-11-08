// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Reads one line of a Serilog JSON file: the compact format (CLEF, <c>CompactJsonFormatter</c> and
/// <c>RenderedCompactJsonFormatter</c>: <c>@t</c>, <c>@l</c>, <c>@mt</c>, <c>@m</c>, <c>@x</c>, <c>@tr</c> and the
/// properties at the top level) and the plain <c>JsonFormatter</c> (<c>Timestamp</c>, <c>Level</c>,
/// <c>MessageTemplate</c>, <c>RenderedMessage</c>, <c>Exception</c> and a <c>Properties</c> object). A line that is
/// not such an object, or whose timestamp is missing or unreadable, is refused (null), never thrown.
/// </summary>
internal static class LogLineParser
{
    private static readonly string[] CorrelationNames = ["CorrelationId", "RequestId", "TraceId"];

    /// <summary>The event of <paramref name="line"/> (UTF-8 JSON), or null when it is not a readable event.</summary>
    public static LogEntry? Parse(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON (a torn write, a plain-text line): the reader counts it as unreadable.
            return null;
        }
    }

    private static LogEntry? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        return root.TryGetProperty("@t", out _) ? ReadCompact(root) : ReadPlain(root);
    }

    private static LogEntry? ReadCompact(JsonElement root)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            // CLEF reserves the leading @ and escapes a property's own as @@.
            if (property.Name.StartsWith("@@", StringComparison.Ordinal))
                Add(properties, order, property.Name[1..], property.Value);
            else if (!property.Name.StartsWith('@'))
                Add(properties, order, property.Name, property.Value);
        }
        return Build(new RawEvent(
            Text(root, "@t"), Text(root, "@l"), Text(root, "@mt"), Text(root, "@m"), Text(root, "@x"), Text(root, "@tr")),
            properties, order);
    }

    private static LogEntry? ReadPlain(JsonElement root)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var order = new List<string>();
        if (root.TryGetProperty("Properties", out var bag) && bag.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in bag.EnumerateObject())
                Add(properties, order, property.Name, property.Value);
        }
        return Build(new RawEvent(
            Text(root, "Timestamp"), Text(root, "Level"), Text(root, "MessageTemplate"), Text(root, "RenderedMessage"),
            Text(root, "Exception"), null),
            properties, order);
    }

    private static void Add(Dictionary<string, JsonElement> properties, List<string> order, string name, JsonElement value)
    {
        if (properties.TryAdd(name, value))
            order.Add(name);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static LogEntry? Build(RawEvent raw, Dictionary<string, JsonElement> properties, List<string> order)
    {
        if (!DateTimeOffset.TryParse(raw.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            return null;
        var message = raw.Rendered ?? (raw.Template is null ? "" : MessageTemplateRenderer.Render(raw.Template, properties));
        var correlationName = CorrelationNames.FirstOrDefault(properties.ContainsKey);
        var shown = new HashSet<string>(StringComparer.Ordinal) { "SourceContext", "RequestMethod", "RequestPath" };
        if (correlationName is not null)
            shown.Add(correlationName);
        var rest = order.Where(name => !shown.Contains(name))
            .Select(name => KeyValuePair.Create(name, MessageTemplateRenderer.Format(properties[name], null)))
            .ToList();
        return new LogEntry(
            timestamp.ToUniversalTime(),
            Level(raw.Level),
            message,
            raw.Template,
            Value(properties, "SourceContext"),
            raw.Exception,
            correlationName is null ? raw.TraceId : Value(properties, correlationName),
            Request(properties),
            rest);
    }

    /// <summary>A Serilog level name; none, or one Serilog does not write, reads as Information.</summary>
    internal static LogSeverity Level(string? name) =>
        Enum.TryParse<LogSeverity>(name, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogSeverity.Information;

    private static string? Value(Dictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) ? MessageTemplateRenderer.Format(value, null) : null;

    private static string? Request(Dictionary<string, JsonElement> properties)
    {
        var path = Value(properties, "RequestPath");
        var method = Value(properties, "RequestMethod");
        return path is null || method is null ? path : method + " " + path;
    }

    private sealed record RawEvent(string? Timestamp, string? Level, string? Template, string? Rendered, string? Exception, string? TraceId);
}
