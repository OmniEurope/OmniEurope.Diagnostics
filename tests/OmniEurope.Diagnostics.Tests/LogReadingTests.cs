// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>Serilog's JSON lines as the page reads them, and real files read within the byte cap.</summary>
public sealed class LogReadingTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LogEntry Parse(string line) => LogLineParser.Parse(Encoding.UTF8.GetBytes(line)) ?? throw new InvalidOperationException("unreadable: " + line);

    [Theory]
    [InlineData(null, nameof(LogSeverity.Information))]
    [InlineData("Verbose", nameof(LogSeverity.Verbose))]
    [InlineData("Debug", nameof(LogSeverity.Debug))]
    [InlineData("Warning", nameof(LogSeverity.Warning))]
    [InlineData("error", nameof(LogSeverity.Error))]
    [InlineData("Fatal", nameof(LogSeverity.Fatal))]
    [InlineData("Bogus", nameof(LogSeverity.Information))]
    [InlineData("42", nameof(LogSeverity.Information))]
    public void TheLevel_IsSerilogs_AndAnAbsentOrUnknownOneIsInformation(string? level, string expected) =>
        Assert.Equal(Enum.Parse<LogSeverity>(expected), Parse(Clef.Line(Clef.Noon, "x", level)).Level);

    [Fact]
    public void ACompactLine_GivesEveryField_AndTheOtherPropertiesInOrder()
    {
        var entry = Parse(Clef.Line(Clef.Noon, "Failed {Count} orders", "Error", "Shop.Orders", "System.Exception: boom",
            properties: [("Count", 3), ("RequestId", "0HN:1"), ("RequestPath", "/api/orders"), ("RequestMethod", "POST"),
                ("MachineName", "web-01"), ("Payload", new { a = 1 }), ("@@at", "escaped")]));

        Assert.Equal((Clef.Noon, LogSeverity.Error, "Failed 3 orders", "Failed {Count} orders"), (entry.Timestamp, entry.Level, entry.Message, entry.Template));
        Assert.Equal(("Shop.Orders", "System.Exception: boom", "0HN:1", "POST /api/orders"), (entry.Category, entry.Exception, entry.CorrelationId, entry.Request));
        Assert.Equal(["Count=3", "MachineName=web-01", "Payload={\"a\":1}", "@at=escaped"], entry.Properties.Select(p => p.Key + "=" + p.Value));
    }

    [Fact]
    public void TheRenderedMessage_WinsOverTheTemplate()
    {
        var entry = Parse(Clef.Line(Clef.Noon, "Hello {Name}", rendered: "Hello \"bob\"", properties: [("Name", "bob")]));

        Assert.Equal("Hello \"bob\"", entry.Message);
    }

    [Theory]
    [InlineData("{Name} paid {Amount:0.00} on {At:yyyy-MM-dd}", "bob paid 12.50 on 2026-11-08")]
    [InlineData("{@Name} {$Name} {Name,10} {0}", "bob bob bob zero")]
    [InlineData("{{literal}} {Missing} {Name", "{literal} {Missing} {Name")]
    [InlineData("count {Whole:D4} and {Amount:D} done }", "count 0042 and 12.5 done }")]
    [InlineData("{Flag} {Nothing} {Whole:zz}", "true null zz")]
    public void TheTemplate_IsRendered_WhenTheFileHasNoRenderedMessage(string template, string expected)
    {
        var entry = Parse(Clef.Line(Clef.Noon, template, properties: [("Name", "bob"), ("Amount", 12.5), ("At", "2026-11-08T10:00:00Z"),
            ("0", "zero"), ("Whole", 42), ("Flag", true), ("Nothing", null!)]));

        Assert.Equal(expected, entry.Message);
    }

    [Fact]
    public void AFormatOnText_LeavesTheTextAsWritten() =>
        Assert.Equal("plain", MessageTemplateRenderer.Format(JsonDocument.Parse("\"plain\"").RootElement, "yyyy"));

    [Fact]
    public void TheCorrelation_PrefersCorrelationId_ThenRequestId_ThenTheTraceId()
    {
        Assert.Equal("c-1", Parse(Clef.Line(Clef.Noon, "x", properties: [("RequestId", "r-1"), ("CorrelationId", "c-1")])).CorrelationId);
        Assert.Equal("t-1", Parse(Clef.Line(Clef.Noon, "x", properties: [("TraceId", "t-1")])).CorrelationId);
        Assert.Equal("abc", Parse("""{"@t":"2026-11-08T12:00:00Z","@mt":"x","@tr":"abc"}""").CorrelationId);
        var bare = Parse("""{"@t":"2026-11-08T12:00:00Z","@mt":"x","RequestPath":"/a"}""");
        Assert.Equal((null, "/a", ""), (bare.CorrelationId, bare.Request, "" + bare.Category));
    }

    [Fact]
    public void ThePlainJsonFormatter_IsReadToo()
    {
        var entry = Parse("""
            {"Timestamp":"2026-11-08T13:00:00.1234567+01:00","Level":"Warning","MessageTemplate":"Slow {Ms}","RenderedMessage":"Slow 40","Exception":"E","Properties":{"Ms":40,"SourceContext":"App"}}
            """);

        Assert.Equal((Clef.Noon.AddTicks(1234567), LogSeverity.Warning, "Slow 40", "App", "E"), (entry.Timestamp, entry.Level, entry.Message, entry.Category, entry.Exception));
        Assert.Equal(["Ms"], entry.Properties.Select(p => p.Key));
        Assert.Equal("Slow 40", Parse("""{"Timestamp":"2026-11-08T12:00:00Z","MessageTemplate":"Slow {Ms}","Properties":{"Ms":40}}""").Message);
        Assert.Equal("", Parse("""{"Timestamp":"2026-11-08T12:00:00Z","Properties":[]}""").Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"@mt\":\"no timestamp\"}")]
    [InlineData("{\"@t\":\"yesterday\",\"@mt\":\"x\"}")]
    [InlineData("{\"@t\":12,\"@mt\":\"x\"}")]
    public void ALineThatIsNotAnEvent_IsRefused_NeverThrown(string line) =>
        Assert.Null(LogLineParser.Parse(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public async Task ADownload_CopiesTheFileAsItWasWhenOpened_WhileTheSinkKeepsAppending()
    {
        using var source = new AppendingOnFirstRead(Encoding.UTF8.GetBytes("{\"@t\":\"2026-11-08T12:00:00Z\",\"@m\":\"a\"}\n"));
        using var body = new MemoryStream();

        await LogFileReader.CopySnapshotAsync(source, body, TestContext.Current.CancellationToken);

        Assert.Equal(source.InitialLength, body.Length);
        Assert.True(source.Length > source.InitialLength);
    }

    [Fact]
    public async Task ADownload_OfAFileCutShorterMeanwhile_EndsWithWhatIsLeft()
    {
        using var source = new ShrinkingOnFirstRead(new byte[200_000], keep: 100_000);
        using var body = new MemoryStream();

        await LogFileReader.CopySnapshotAsync(source, body, TestContext.Current.CancellationToken);

        Assert.Equal(100_000, body.Length);
    }

    /// <summary>A file the sink appends to as soon as it is read, like a log file downloaded while the site writes.</summary>
    private sealed class AppendingOnFirstRead : MemoryStream
    {
        private bool _appended;

        public AppendingOnFirstRead(byte[] content)
        {
            Write(content);
            Position = 0;
            InitialLength = content.Length;
        }

        public long InitialLength { get; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_appended)
            {
                _appended = true;
                var position = Position;
                Position = Length;
                Write(Encoding.UTF8.GetBytes("{\"@t\":\"2026-11-08T12:00:01Z\",\"@m\":\"written during the download\"}\n"));
                Position = position;
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>A file cut shorter as soon as it is read, like a log file a retention job truncates.</summary>
    private sealed class ShrinkingOnFirstRead : MemoryStream
    {
        private readonly int _keep;
        private bool _cut;

        public ShrinkingOnFirstRead(byte[] content, int keep) : base()
        {
            Write(content);
            Position = 0;
            _keep = keep;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_cut)
            {
                _cut = true;
                SetLength(_keep);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public void AMalformedLine_IsSkippedAndCounted_WithItsNumber_AndATornLastLineIsLeftForLater()
    {
        var path = _folder.Write("app.json", [Clef.Line(Clef.Noon, "one"), "garbage", "", Clef.Line(Clef.Noon, "two"), "{\"broken\":"]);
        File.AppendAllText(path, "{\"@t\":\"2026-11-08T12:00:00Z\",\"@mt\":\"torn"); // being written: no end of line yet

        var read = LogFileReader.Read([new FileInfo(path)], LogsOptions.DefaultMaxBytesRead);

        Assert.Equal(["one", "two"], read.Entries.Select(e => e.Message));
        Assert.Equal([1, 4], read.Entries.Select(e => e.Line!.Value));
        var issue = Assert.Single(read.Issues);
        Assert.Equal(("app.json", false, 2), (issue.File, issue.Failed, issue.UnreadableCount));
        Assert.Equal([2, 5], issue.Lines);
        Assert.False(read.Truncated);
    }

    [Fact]
    public void ACompleteLastLine_WithoutItsEndOfLine_IsRead_AndAByteOrderMarkIsSkipped()
    {
        var path = _folder.File("bom.json");
        File.WriteAllText(path, Clef.Line(Clef.Noon, "first") + "\r\n" + Clef.Line(Clef.Noon, "last"), new UTF8Encoding(true));

        var read = LogFileReader.Read([new FileInfo(path)], LogsOptions.DefaultMaxBytesRead);

        Assert.Equal(["first", "last"], read.Entries.Select(e => e.Message));
        Assert.Empty(read.Issues);
    }

    [Fact]
    public void OnlyTheFirstTenUnreadableLineNumbers_AreKept()
    {
        var path = _folder.Write("bad.json", Enumerable.Repeat("bad", 12));

        var issue = Assert.Single(LogFileReader.Read([new FileInfo(path)], LogsOptions.DefaultMaxBytesRead).Issues);

        Assert.Equal((12, 10), (issue.UnreadableCount, issue.Lines.Count));
    }

    [Fact]
    public void TheCap_ReadsTheNewestFileFirst_AndALargeFileFromItsEnd()
    {
        var older = _folder.Write("older.json", [Clef.Line(Clef.Noon, "old")]);
        var lines = Enumerable.Range(1, 100).Select(i => Clef.Line(Clef.Noon.AddSeconds(i), "line " + i)).ToList();
        var newest = _folder.Write("newest.json", lines);
        var budget = Encoding.UTF8.GetByteCount(string.Concat(lines.TakeLast(3).Select(l => l + "\n"))) + 5;

        var read = LogFileReader.Read([new FileInfo(newest), new FileInfo(older)], budget);

        Assert.True(read.Truncated);
        Assert.Equal(["line 98", "line 99", "line 100"], read.Entries.Select(e => e.Message));
        Assert.All(read.Entries, entry => Assert.Null(entry.Line));
        Assert.Empty(read.Issues); // the cut line is a fragment, not an unreadable line
    }

    [Fact]
    public void ACapSmallerThanOneLine_ReadsNothing_AndSaysItWasCut()
    {
        var path = _folder.File("one.json");
        File.WriteAllText(path, Clef.Line(Clef.Noon, new string('x', 200))); // no end of line at all

        var read = LogFileReader.Read([new FileInfo(path)], 50);

        Assert.True(read.Truncated);
        Assert.Empty(read.Entries);
        Assert.Empty(read.Issues);
    }

    [Fact]
    public void AnExhaustedCap_LeavesTheOlderFilesUnread()
    {
        var first = _folder.Write("a.json", [Clef.Line(Clef.Noon, "a")]);
        var second = _folder.Write("b.json", [Clef.Line(Clef.Noon, "b")]);

        var read = LogFileReader.Read([new FileInfo(first), new FileInfo(second)], new FileInfo(first).Length);

        Assert.Equal(["a"], read.Entries.Select(e => e.Message));
        Assert.True(read.Truncated);
    }

    [Fact]
    public void AFileTheSinkIsWriting_IsReadWithoutBlockingIt()
    {
        var path = _folder.Write("live.json", [Clef.Line(Clef.Noon, "before")]);
        using var sink = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        var read = LogFileReader.Read([new FileInfo(path)], LogsOptions.DefaultMaxBytesRead);
        sink.Write(Encoding.UTF8.GetBytes(Clef.Line(Clef.Noon, "after") + "\n"));

        Assert.Equal(["before"], read.Entries.Select(e => e.Message));
    }

    [Fact]
    public void AFileThatCannotBeOpened_IsNamed_AndTheOthersAreStillRead()
    {
        var locked = _folder.Write("locked.json", [Clef.Line(Clef.Noon, "hidden")]);
        var fine = _folder.Write("fine.json", [Clef.Line(Clef.Noon, "shown")]);
        var folderAsFile = Directory.CreateDirectory(_folder.File("folder.json"));
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var read = LogFileReader.Read([new FileInfo(locked), new FileInfo(folderAsFile.FullName), new FileInfo(fine)], LogsOptions.DefaultMaxBytesRead);

        Assert.Equal(["shown"], read.Entries.Select(e => e.Message));
        Assert.Equal(["locked.json", "folder.json"], read.Issues.Select(issue => issue.File));
        Assert.All(read.Issues, issue => Assert.True(issue.Failed));
    }
}
