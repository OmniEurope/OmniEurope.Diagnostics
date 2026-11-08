// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The flat JSON format a simple writer produces: the event's fields and its properties at the top level.</summary>
public sealed class LogFlatFormatTests
{
    private static LogEntry Parse(string line) => LogLineParser.Parse(Encoding.UTF8.GetBytes(line)) ?? throw new InvalidOperationException("unreadable: " + line);

    [Fact]
    public void AFlatLine_GivesEveryField_AndTheOtherTopLevelPropertiesInOrder()
    {
        var entry = Parse("""
            {"Timestamp":"2026-11-08T12:00:00Z","Level":"Warning","Message":"Disk low","SourceContext":"App.Disk","Exception":"System.IO.IOException: full","CorrelationId":"c-1","FreeMb":12,"RequestPath":"/upload","RequestMethod":"PUT","Machine":"web-01"}
            """);

        Assert.Equal((Clef.Noon, LogSeverity.Warning, "Disk low", (string?)null), (entry.Timestamp, entry.Level, entry.Message, entry.Template));
        Assert.Equal(("App.Disk", "System.IO.IOException: full", "c-1", "PUT /upload"), (entry.Category, entry.Exception, entry.CorrelationId, entry.Request));
        Assert.Equal(["FreeMb=12", "Machine=web-01"], entry.Properties.Select(p => p.Key + "=" + p.Value));
    }

    [Fact]
    public void AFlatTemplate_IsRenderedFromTheTopLevelProperties_AndTheRenderedMessageWinsOverMessage()
    {
        Assert.Equal("Free 12 MB", Parse("""{"Timestamp":"2026-11-08T12:00:00Z","MessageTemplate":"Free {FreeMb} MB","FreeMb":12}""").Message);
        Assert.Equal("rendered", Parse("""{"Timestamp":"2026-11-08T12:00:00Z","RenderedMessage":"rendered","Message":"flat"}""").Message);
        Assert.Equal("flat", Parse("""{"Timestamp":"2026-11-08T12:00:00Z","MessageTemplate":"Free {FreeMb} MB","Message":"flat","FreeMb":12}""").Message);
    }

    [Fact]
    public void APropertiesObject_WinsOverTheTopLevel_AsInSerilogsJsonFormatter()
    {
        var entry = Parse("""{"Timestamp":"2026-11-08T12:00:00Z","Message":"m","Properties":{"A":1},"Top":2,"Renderings":{"A":[]}}""");

        Assert.Equal(["A"], entry.Properties.Select(p => p.Key));
        Assert.Empty(Parse("""{"Timestamp":"2026-11-08T12:00:00Z","Message":"m","Properties":[],"Renderings":{}}""").Properties);
    }

    [Fact]
    public void AFlatLineWithoutTimestamp_IsRefused() =>
        Assert.Null(LogLineParser.Parse(Encoding.UTF8.GetBytes("""{"Level":"Error","Message":"no time"}""")));

    [Theory]
    [InlineData("Trace", nameof(LogSeverity.Verbose))]
    [InlineData("critical", nameof(LogSeverity.Fatal))]
    [InlineData("Information", nameof(LogSeverity.Information))]
    public void MicrosoftsLevelNames_AreRead(string level, string expected) =>
        Assert.Equal(Enum.Parse<LogSeverity>(expected), Parse($$"""{"Timestamp":"2026-11-08T12:00:00Z","Level":"{{level}}","Message":"m"}""").Level);
}
