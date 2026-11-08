// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OmniEurope.Diagnostics.FileLogging;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The lines the file logger writes: the compact Serilog JSON format, read back from a real file.</summary>
public sealed class FileLoggingFormatTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(LogLevel.Trace, "Verbose")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, null)]
    [InlineData(LogLevel.Warning, "Warning")]
    [InlineData(LogLevel.Error, "Error")]
    [InlineData(LogLevel.Critical, "Fatal")]
    public void TheLevel_IsSerilogsName_AndInformationIsLeftOut(LogLevel level, string? expected)
    {
        using (var provider = FileLogs.Provider(_folder.Path, options => options.MinLevel = LogLevel.Trace))
            provider.CreateLogger("Cat").Log(level, "x");

        Assert.Equal(expected, Assert.Single(FileLogs.Entries(_folder.File("app-20261108.json"))).Text("@l"));
    }

    [Fact]
    public void AnEntry_CarriesItsTimeTemplateMessageExceptionCategoryEventIdAndProperties()
    {
        Activity.Current = null;
        var error = new InvalidOperationException("boom");

        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path,
            logger => logger.LogError(new EventId(7, "Saved"), error, "Failed {Count} orders for {Customer}", 3, "ACME")));

        Assert.Equal("2026-11-08T12:00:00.0000000Z", entry.Text("@t"));
        Assert.Equal(("Error", "Failed {Count} orders for {Customer}", "Failed 3 orders for ACME"), (entry.Text("@l"), entry.Text("@mt"), entry.Text("@m")));
        Assert.Equal(error.ToString(), entry.Text("@x"));
        Assert.Equal("Shop.Orders", entry.Text("SourceContext"));
        Assert.Equal((7, "Saved"), (entry.GetProperty("EventId").GetProperty("Id").GetInt32(), entry.GetProperty("EventId").GetProperty("Name").GetString()));
        Assert.Equal((JsonValueKind.Number, 3), (entry.GetProperty("Count").ValueKind, entry.GetProperty("Count").GetInt32()));
        Assert.Equal("ACME", entry.Text("Customer"));
        Assert.False(entry.TryGetProperty("{OriginalFormat}", out _));
        Assert.False(entry.TryGetProperty("@tr", out _));
    }

    [Fact]
    public void AnEventIdWithoutName_KeepsItsNumber_AndAZeroIdIsLeftOut()
    {
        var entries = FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            logger.LogInformation(new EventId(12), "numbered");
            logger.LogInformation("plain");
        });

        Assert.Equal("{\"Id\":12}", entries[0].GetProperty("EventId").GetRawText());
        Assert.False(entries[1].TryGetProperty("EventId", out _));
    }

    [Fact]
    public void AStateWithoutTemplate_GivesTheRenderedMessageOnly()
    {
        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path,
            logger => logger.Log(LogLevel.Information, default, "state text", null, (state, _) => "rendered " + state)));

        Assert.Equal(("rendered state text", null), (entry.Text("@m"), entry.Text("@mt")));
    }

    [Fact]
    public void APropertyStartingWithAt_IsEscaped_AndReservedNamesKeepTheirMeaning()
    {
        var state = new KeyValuePair<string, object?>[] { new("@at", "escaped"), new("SourceContext", "spoofed"), new("Plain", 1) };

        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path,
            logger => logger.Log(LogLevel.Information, default, state, null, (_, _) => "m")));

        Assert.Equal(("escaped", "Shop.Orders", "1"), (entry.Text("@@at"), entry.Text("SourceContext"), entry.Text("Plain")));
        Assert.False(entry.TryGetProperty("@at", out _));
    }

    [Fact]
    public void ScopeProperties_GoToTheTopLevel_TheEntrysOwnValueWins_AndTextScopesAreListed()
    {
        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "0HN:1", ["RequestPath"] = "/api", ["Count"] = 99 }))
            using (logger.BeginScope("plain text"))
            using (logger.BeginScope(new object()))
                logger.LogInformation("Saved {Count}", 3);
        }, provider => provider.SetScopeProvider(new LoggerExternalScopeProvider())));

        Assert.Equal(("0HN:1", "/api", "3"), (entry.Text("RequestId"), entry.Text("RequestPath"), entry.Text("Count")));
        Assert.Equal(["plain text", "System.Object"], entry.GetProperty("Scope").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void AStatePropertyNamedScope_IsNotOverwrittenByTheTextScopes()
    {
        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            using (logger.BeginScope("plain text"))
                logger.LogInformation("In {Scope}", "checkout");
        }, provider => provider.SetScopeProvider(new LoggerExternalScopeProvider())));

        Assert.Equal("checkout", entry.Text("Scope"));
    }

    [Fact]
    public void WithoutScopeProvider_BeginScopeIsHarmless_AndNoScopeIsWritten()
    {
        var entry = Assert.Single(FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            using (logger.BeginScope("ignored"))
                logger.LogInformation("m");
        }));

        Assert.False(entry.TryGetProperty("Scope", out _));
    }

    [Fact]
    public void TheCurrentW3CActivity_GivesTheTraceAndSpan_AHierarchicalOneDoesNot()
    {
        using var w3c = new Activity("w3c").SetIdFormat(ActivityIdFormat.W3C).Start();
        var entries = FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            logger.LogInformation("traced");
            using var hierarchical = new Activity("old").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
            logger.LogInformation("hierarchical");
        });

        Assert.Equal((w3c.TraceId.ToHexString(), w3c.SpanId.ToHexString()), (entries[0].Text("@tr"), entries[0].Text("@sp")));
        Assert.False(entries[1].TryGetProperty("@tr", out _));
    }

    [Fact]
    public void Values_ArePrimitivesAsJson_DatesAsIso_AndTheRestAsInvariantText()
    {
        var cases = new (object? Value, string Json)[]
        {
            (null, "null"), ("x", "\"x\""), (true, "true"), ((byte)1, "1"), ((sbyte)-1, "-1"), ((short)-2, "-2"),
            ((ushort)2, "2"), (-3, "-3"), (3u, "3"), (-4L, "-4"), (ulong.MaxValue, "18446744073709551615"),
            (1.25m, "1.25"), (2.5d, "2.5"), (double.NaN, "\"NaN\""), (1.5f, "1.5"), (float.PositiveInfinity, "\"Infinity\""),
            (new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc), "\"2026-11-08T12:00:00.0000000Z\""),
            (new DateTimeOffset(2026, 11, 8, 13, 0, 0, TimeSpan.FromHours(1)), "\"2026-11-08T13:00:00.0000000+01:00\""),
            (TimeSpan.FromMinutes(90), "\"01:30:00\""), (LogLevel.Warning, "\"Warning\""), (new Uri("https://example.org/a"), "\"https://example.org/a\""), (new object(), "\"System.Object\""),
        };
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            foreach (var (value, expected) in cases)
                Assert.Equal(expected, Json(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AValueWhoseTextThrows_LosesOnlyThatEntry_AndIsReportedAsDropped()
    {
        var entries = FileLogs.WriteAndRead(_folder.Path, logger =>
        {
            logger.LogInformation("Bad {Value}", new ThrowingText());
            logger.LogInformation("good");
        });

        Assert.Equal(["good", "1 log entries were dropped: the write queue was full or the log file could not be written."], entries.Select(entry => entry.Text("@m")));
    }

    [Fact]
    public void BelowTheMinimumLevel_NothingIsWritten_AndNoneIsNeverEnabled()
    {
        using (var provider = FileLogs.Provider(_folder.Path, options => options.MinLevel = LogLevel.Warning))
        {
            var logger = provider.CreateLogger("Cat");
            Assert.Same(logger, provider.CreateLogger("Cat"));
            Assert.False(logger.IsEnabled(LogLevel.None));
            logger.LogInformation("hidden");
            logger.LogWarning("shown");
            logger.Log(LogLevel.None, "never");
        }

        Assert.Equal(["shown"], FileLogs.Entries(_folder.File("app-20261108.json")).Select(entry => entry.Text("@m")));
    }

    [Fact]
    public void AccentedText_IsWrittenAsIs()
    {
        var line = Assert.Single(FileLogs.Lines(WriteOne("Opération réussie")));

        Assert.Contains("Opération réussie", line, StringComparison.Ordinal);
    }

    private string WriteOne(string message)
    {
        FileLogs.WriteAndRead(_folder.Path, logger => logger.LogInformation(message));
        return _folder.File("app-20261108.json");
    }

    private static string Json(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            ClefFormatter.WriteValue(json, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class ThrowingText
    {
        public override string ToString() => throw new FormatException("no text");
    }
}
