// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>One entry to format: what the logger knows besides its state and scopes.</summary>
internal readonly record struct ClefEvent(
    DateTimeOffset Timestamp, LogLevel Level, string Category, EventId EventId, string Message, Exception? Exception);

/// <summary>
/// Writes an entry as one line of the compact Serilog JSON format (CLEF): <c>@t</c>, <c>@l</c> (absent for
/// Information), <c>@mt</c>, <c>@m</c>, <c>@x</c>, <c>@tr</c> and <c>@sp</c>, then <c>SourceContext</c>,
/// <c>EventId</c>, the state's and the scopes' properties at the top level, a leading <c>@</c> of their own escaped
/// as <c>@@</c>. The first property of a name wins, so a scope never overrides the entry's own value. In
/// confidential mode only the template is written, never the rendered message nor the values of the state's and the
/// scopes' properties, and the exception keeps its type, HResult and throwing method, never its message nor its
/// stack trace (see <see cref="FileLoggingOptions.Confidential"/>).
/// </summary>
internal static class ClefFormatter
{
    private const string OriginalFormat = "{OriginalFormat}";

    // The file is read as JSON, never embedded in HTML: accented letters stay readable.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(ClefEvent entry, object? state, IExternalScopeProvider? scopes, Activity? activity, bool confidential = false)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("@t", entry.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            if (LevelName(entry.Level) is { } level)
                json.WriteString("@l", level);
            var properties = state as IEnumerable<KeyValuePair<string, object?>>;
            if (Template(properties) is { } template)
                json.WriteString("@mt", template);
            if (!confidential)
                json.WriteString("@m", entry.Message);
            if (entry.Exception is not null)
                json.WriteString("@x", confidential ? Outline(entry.Exception) : entry.Exception.ToString());
            if (activity is { IdFormat: ActivityIdFormat.W3C })
            {
                json.WriteString("@tr", activity.TraceId.ToHexString());
                json.WriteString("@sp", activity.SpanId.ToHexString());
            }
            json.WriteString("SourceContext", entry.Category);
            if (entry.EventId.Id != 0)
                WriteEventId(json, entry.EventId);
            var written = new HashSet<string>(StringComparer.Ordinal) { "SourceContext", "EventId" };
            if (!confidential)
            {
                WriteProperties(json, properties, written);
                WriteScopes(json, scopes, written);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Serilog's name of <paramref name="level"/>; none for Information, the format's default.</summary>
    internal static string? LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose",
        LogLevel.Debug => "Debug",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => null,
    };

    /// <summary>
    /// The exception and its inner ones without what they may hold of the user's data: one line each, its type, its
    /// HResult and the method that threw it, never its message nor its stack trace.
    /// </summary>
    internal static string Outline(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var site = current.TargetSite is { } method ? " in " + (method.DeclaringType is { } type ? type.FullName + "." : "") + method.Name : "";
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{current.GetType().FullName} (HResult 0x{current.HResult:X8}){site}"));
        }
        return string.Join("\n ---> ", lines);
    }

    private static string? Template(IEnumerable<KeyValuePair<string, object?>>? properties) =>
        properties?.FirstOrDefault(pair => pair.Key == OriginalFormat).Value as string;

    private static void WriteEventId(Utf8JsonWriter json, EventId eventId)
    {
        json.WriteStartObject("EventId");
        json.WriteNumber("Id", eventId.Id);
        if (eventId.Name is not null)
            json.WriteString("Name", eventId.Name);
        json.WriteEndObject();
    }

    private static void WriteProperties(Utf8JsonWriter json, IEnumerable<KeyValuePair<string, object?>>? properties, HashSet<string> written)
    {
        if (properties is null)
            return;
        foreach (var (name, value) in properties)
        {
            if (name != OriginalFormat && written.Add(name))
            {
                json.WritePropertyName(name.StartsWith('@') ? "@" + name : name);
                WriteValue(json, value);
            }
        }
    }

    private static void WriteScopes(Utf8JsonWriter json, IExternalScopeProvider? scopes, HashSet<string> written)
    {
        var texts = new List<string>();
        scopes?.ForEachScope((scope, _) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                WriteProperties(json, pairs, written);
            else if (scope?.ToString() is { } text)
                texts.Add(text);
        }, 0);
        // Scopes without properties (a plain text, say) are kept in order, as Serilog keeps them.
        if (texts.Count == 0 || !written.Add("Scope"))
            return;
        json.WriteStartArray("Scope");
        foreach (var text in texts)
            json.WriteStringValue(text);
        json.WriteEndArray();
    }

    /// <summary>Numbers, booleans and null as JSON values, dates as ISO 8601, anything else as its text.</summary>
    internal static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            default:
                if (!TryWriteNumber(json, value))
                    json.WriteStringValue(Text(value));
                break;
        }
    }

    /// <summary>Writes a finite number as a JSON number; false for anything else, NaN and infinities included.</summary>
    private static bool TryWriteNumber(Utf8JsonWriter json, object value)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            case ulong large:
                json.WriteNumberValue(large);
                return true;
            case decimal exact:
                json.WriteNumberValue(exact);
                return true;
            case double real when double.IsFinite(real):
                json.WriteNumberValue(real);
                return true;
            case float single when float.IsFinite(single):
                json.WriteNumberValue(single);
                return true;
            default:
                return false;
        }
    }

    private static string? Text(object value) => value switch
    {
        string text => text,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
