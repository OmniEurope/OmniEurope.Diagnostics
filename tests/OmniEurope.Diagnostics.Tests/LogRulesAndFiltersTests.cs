// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The filters, the exclusion rules and their file, the folder listing and the options, without a host.</summary>
public sealed class LogRulesAndFiltersTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LogEntry Entry(string message, LogSeverity level = LogSeverity.Information, string? category = null,
        string? exception = null, string? correlation = null, int minutes = 0, long sequence = 0) =>
        new(Clef.Noon.AddMinutes(minutes), level, message, null, category, exception, correlation, null, []) { Sequence = sequence };

    private static LogQuery Query(params (string Name, string Value)[] values) =>
        LogQuery.Parse(new QueryCollection(values.ToDictionary(v => v.Name, v => new StringValues(v.Value))));

    private LogDirectory Directory(Action<LogsOptions>? configure = null)
    {
        var options = new LogsOptions { Directory = _folder.Path };
        configure?.Invoke(options);
        return new LogDirectory(Options.Create(options), new Environment(_folder.Path));
    }

    [Fact]
    public void TheMinimumLevel_KeepsThatLevelAndAbove()
    {
        var query = Query(("level", "warning"));

        Assert.Equal(LogSeverity.Warning, query.MinLevel);
        Assert.False(query.Accepts(Entry("a", LogSeverity.Information)));
        Assert.True(query.Accepts(Entry("a", LogSeverity.Warning)));
        Assert.True(query.Accepts(Entry("a", LogSeverity.Fatal)));
        Assert.Null(Query(("level", "Loud")).MinLevel);
    }

    [Fact]
    public void FromAndTo_AreUtcBoundsIncluded_AndAnUnreadableDateIsIgnored()
    {
        var query = Query(("from", "2026-11-08T12:00"), ("to", "2026-11-08T12:10:30"));

        Assert.Equal(("2026-11-08T12:00", "2026-11-08T12:10:30"), (query.From, query.To));
        Assert.False(query.Accepts(Entry("a", minutes: -1)));
        Assert.True(query.Accepts(Entry("a", minutes: 0)));
        Assert.True(query.Accepts(Entry("a", minutes: 10)));
        Assert.False(query.Accepts(Entry("a", minutes: 11)));
        Assert.Null(Query(("from", "tomorrow")).FromDate);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("category")]
    [InlineData("exception")]
    [InlineData("correlation")]
    public void TheText_IsSearchedInTheMessage_TheCategory_TheException_AndTheCorrelation(string field)
    {
        var query = Query(("q", "NEEDLE"));
        var entry = Entry(field == "message" ? "a needle here" : "plain", category: field == "category" ? "Shop.Needle" : "Shop",
            exception: field == "exception" ? "NeedleException" : null, correlation: field == "correlation" ? "needle:4" : "0HN:1");

        Assert.True(query.Accepts(entry));
        Assert.False(query.Accepts(Entry("plain", category: "Shop", correlation: "0HN:1")));
    }

    [Fact]
    public void TheQuery_RoundTripsThroughItsQueryString_AndAnUnreadablePageIsTheFirst()
    {
        var query = Query(("tab", "excluded"), ("file", "app.json"), ("level", "Error"), ("from", "2026-11-08T00:00"),
            ("to", "2026-11-08T23:59:59.5"), ("q", "a b"), ("page", "3"), ("follow", "1"), ("format", "csv"));

        Assert.Equal(query, Query(QueryHelpers(query.ToQueryString())));
        Assert.Equal(query with { Page = 1, Follow = false, Csv = false }, Query(QueryHelpers(query.Filters().ToQueryString())));
        Assert.DoesNotContain("page=", query.Filters().ToQueryString(), StringComparison.Ordinal);
        Assert.Equal("", new LogQuery().ToQueryString());
        Assert.Equal(1, Query(("page", "-2")).Page);
        Assert.Equal(1, Query(("page", "0")).Page);
    }

    private static (string, string)[] QueryHelpers(string queryString) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString).Select(pair => (pair.Key, pair.Value.ToString())).ToArray();

    [Fact]
    public void ARule_MatchesItsTextIgnoringCase_AndItsCategoryExactly()
    {
        var anyCategory = new LogRule(1, "HTTPS PORT", null, Enabled: true);
        var oneCategory = new LogRule(2, "https port", "microsoft.aspnetcore.httpspolicy", Enabled: true);

        Assert.True(anyCategory.Matches(Entry("Failed to determine the https port for redirect.", category: "Other")));
        Assert.True(oneCategory.Matches(Entry("Failed to determine the https port", category: "Microsoft.AspNetCore.HttpsPolicy")));
        Assert.False(oneCategory.Matches(Entry("Failed to determine the https port", category: "Microsoft.AspNetCore.HttpsPolicy.Middleware")));
        Assert.False(oneCategory.Matches(Entry("Failed to determine the https port")));
        Assert.False(anyCategory.Matches(Entry("unrelated")));
    }

    [Fact]
    public void TheSplit_SendsMatchedEntriesToTheExcludedTab_WithTheirRule_IgnoresDisabledRules_AndListsNewestFirst()
    {
        LogEntry[] entries = [Entry("old noise", minutes: 1, sequence: 0), Entry("keep", minutes: 2, sequence: 1), Entry("new noise", minutes: 3, sequence: 2),
            Entry("same instant, first line", minutes: 4, sequence: 3), Entry("same instant, second line", minutes: 4, sequence: 4), Entry("disabled", minutes: 5)];
        LogRule[] rules = [new(1, "noise", null, Enabled: true), new(2, "disabled", null, Enabled: false)];

        var (journal, excluded) = LogSelection.Split(entries, rules, new LogQuery());

        Assert.Equal(["disabled", "same instant, second line", "same instant, first line", "keep"], journal.Select(row => row.Entry.Message));
        Assert.Equal(["new noise", "old noise"], excluded.Select(row => row.Entry.Message));
        Assert.All(excluded, row => Assert.Equal(1, row.Rule!.Id));
        Assert.All(journal, row => Assert.Null(row.Rule));
        Assert.Single(LogSelection.Split(entries, rules, Query(("q", "keep"))).Journal);
    }

    [Fact]
    public void TheRules_RoundTripThroughTheirFile_AndEachChangeIsSaved()
    {
        var store = new LogRuleStore(Directory());

        Assert.Empty(store.Load().Rules);
        Assert.Equal(LogRuleUpdate.Saved, store.Add("first", null));
        Assert.Equal(LogRuleUpdate.Saved, store.Add("second", "Cat"));
        Assert.Equal(LogRuleUpdate.Saved, store.SetEnabled(1, enabled: false));
        Assert.Equal(LogRuleUpdate.Saved, store.Delete(2));
        Assert.Equal(LogRuleUpdate.Saved, store.Add("third", null));
        Assert.Equal(LogRuleUpdate.NotFound, store.SetEnabled(42, enabled: true));
        Assert.Equal(LogRuleUpdate.NotFound, store.Delete(42));

        var reread = new LogRuleStore(Directory()).Load();
        Assert.Null(reread.Error);
        Assert.Equal([new LogRule(1, "first", null, false), new LogRule(3, "third", null, true)], reread.Rules);
        Assert.Equal(["omni-log-rules.json"], System.IO.Directory.GetFiles(_folder.Path).Select(Path.GetFileName)); // no temporary file left
    }

    [Fact]
    public async Task ConcurrentAdds_NeverLoseARule()
    {
        var store = new LogRuleStore(Directory());

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => store.Add("rule " + i, null), TestContext.Current.CancellationToken)));

        var rules = store.Load().Rules;
        Assert.Equal(24, rules.Count);
        Assert.Equal(Enumerable.Range(1, 24), rules.Select(rule => rule.Id).Order());
    }

    [Fact]
    public void AnUnreadableRulesFile_IsReported_AndNeverOverwritten()
    {
        var path = _folder.Write("omni-log-rules.json", ["{ not json"]);
        var store = new LogRuleStore(Directory());

        Assert.NotNull(store.Load().Error);
        Assert.Equal(LogRuleUpdate.Unreadable, store.Add("x", null));
        Assert.Equal("{ not json\n", File.ReadAllText(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.NotNull(store.Load().Error);
        File.WriteAllText(path, """{"rules":[{"id":1,"pattern":" "},null,{"id":2,"pattern":"kept","enabled":true}]}""");
        Assert.Equal(["kept"], store.Load().Rules.Select(rule => rule.Pattern));
        File.WriteAllText(path, "null");
        Assert.Empty(store.Load().Rules);
    }

    [Fact]
    public void TheListing_HoldsTheMatchingFilesOnly_NewestFirst_NeverTheRulesFileNorASubFolder()
    {
        _folder.Write("app-1.json", ["{}"], new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc));
        _folder.Write("app-2.json", ["{}"], new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        _folder.Write("notes.txt", ["{}"]);
        _folder.Write("omni-log-rules.json", ["{}"]);
        _folder.Write("omni-log-rules.json.0a1b.tmp.json", ["{}"]);
        System.IO.Directory.CreateDirectory(_folder.File("nested"));
        File.WriteAllText(Path.Combine(_folder.File("nested"), "deep.json"), "{}");

        Assert.Equal(["app-2.json", "app-1.json"], Directory().List().Select(file => file.Name));
        Assert.Equal(["notes.txt"], Directory(o => o.FilePattern = "*.txt").List().Select(file => file.Name));
        Assert.Empty(Directory(o => o.Directory = Path.Combine(_folder.Path, "absent")).List());
    }

    [Theory]
    [InlineData("app.json", nameof(LogFileLookup.Found))]
    [InlineData("APP.JSON", nameof(LogFileLookup.Found))]
    [InlineData(null, nameof(LogFileLookup.None))]
    [InlineData("", nameof(LogFileLookup.None))]
    [InlineData("other.json", nameof(LogFileLookup.Missing))]
    [InlineData("..\\app.json", nameof(LogFileLookup.Invalid))]
    [InlineData("../app.json", nameof(LogFileLookup.Invalid))]
    [InlineData("sub/app.json", nameof(LogFileLookup.Invalid))]
    [InlineData("C:\\app.json", nameof(LogFileLookup.Invalid))]
    [InlineData("C:app.json", nameof(LogFileLookup.Invalid))]
    [InlineData("/etc/passwd", nameof(LogFileLookup.Invalid))]
    [InlineData("..", nameof(LogFileLookup.Invalid))]
    [InlineData("a\0b", nameof(LogFileLookup.Invalid))]
    [InlineData("   ", nameof(LogFileLookup.Invalid))]
    public void ARequestedFile_MustBeABareNameInTheListing(string? name, string expectedName)
    {
        var expected = Enum.Parse<LogFileLookup>(expectedName);
        _folder.Write("app.json", ["{}"]);

        var (lookup, file) = LogDirectory.Find(name, Directory().List());

        Assert.Equal(expected, lookup);
        Assert.Equal(expected == LogFileLookup.Found, file is not null);
    }

    [Fact]
    public void ARelativeDirectory_IsResolvedAgainstTheContentRoot()
    {
        // The platform's own separator: on Linux a backslash is an ordinary file-name character, not a separator.
        var directory = new LogDirectory(Options.Create(new LogsOptions { Directory = "logs" + Path.DirectorySeparatorChar }), new Environment(_folder.Path));

        Assert.Equal(Path.Combine(_folder.Path, "logs") + Path.DirectorySeparatorChar, directory.FullPath);
        Assert.Equal(("logs/", "logs/*.json", "logs/omni-log-rules.json"), (directory.DirectoryLabel, directory.Source, directory.RulesLabel));
    }

    [Fact]
    public void ThePurge_DeletesOnlyOldMatchingFiles_AndCountsWhatItCannotDelete()
    {
        var old = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _folder.Write("old.json", ["{}"], old);
        var locked = _folder.Write("locked.json", ["{}"], old);
        var readOnly = _folder.Write("readonly.json", ["{}"], old);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        _folder.Write("recent.json", ["{}"], new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        _folder.Write("old.txt", ["{}"], old);
        _folder.Write("omni-log-rules.json", ["{}"], old);
        var directory = Directory();
        var now = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(["locked.json", "old.json", "readonly.json"], directory.OlderThan(30, now).Select(f => f.Name).Order());
        LogPurgeResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = directory.Purge(30, now);

        // Windows refuses to delete a file held open without delete sharing, or a read-only one: both are counted as
        // failed and stay. POSIX deletes both (an open file lives on for its reader, read-only guards the content,
        // not the folder entry). Either way only old files matching the pattern go, never the rules file.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new LogPurgeResult(1, 2), result);
            Assert.Equal(["locked.json", "old.txt", "omni-log-rules.json", "readonly.json", "recent.json"],
                System.IO.Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Order());
        }
        else
        {
            Assert.Equal(new LogPurgeResult(3, 0), result);
            Assert.Equal(["old.txt", "omni-log-rules.json", "recent.json"],
                System.IO.Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Order());
        }
    }

    [Fact]
    public void TheCsv_QuotesEveryCell_DoublesQuotes_AndNeutralisesFormulas()
    {
        Assert.Equal("\"\"", LogsCsv.Cell(null));
        Assert.Equal("\"say \"\"hi\"\", ok\"", LogsCsv.Cell("say \"hi\", ok"));
        foreach (var formula in new[] { "=1+1", "+1", "-1", "@SUM(A1)", "\tx", "\rx" })
            Assert.Equal("\"'" + formula + "\"", LogsCsv.Cell(formula));
        Assert.Equal("\"a=b\"", LogsCsv.Cell("a=b"));

        var csv = Encoding.UTF8.GetString(LogsCsv.Write([new LogRow(Entry("=HYPERLINK(\"x\")") with { File = "a.json", Line = 3 }, new LogRule(1, "HYPER", null, true)),
            new LogRow(Entry("plain"), null)]));
        Assert.Equal(
            "\uFEFF\"Timestamp\",\"Level\",\"Category\",\"Message\",\"CorrelationId\",\"Exception\",\"File\",\"Line\",\"Rule\"\r\n"
            + "\"2026-11-08T12:00:00.0000000+00:00\",\"Information\",\"\",\"'=HYPERLINK(\"\"x\"\")\",\"\",\"\",\"a.json\",\"3\",\"HYPER\"\r\n"
            + "\"2026-11-08T12:00:00.0000000+00:00\",\"Information\",\"\",\"plain\",\"\",\"\",\"\",\"\",\"\"\r\n", csv);
    }

    [Fact]
    public void TheSuggestedPattern_IsTheTemplatesLiteralStart_ElseTheMessagesFirstLine()
    {
        Assert.Equal("Failed executing DbCommand (", LogsLinks.SuggestedPattern(Entry("x") with { Template = "Failed executing DbCommand ({elapsed}ms)" }));
        Assert.Equal("Hello", LogsLinks.SuggestedPattern(Entry("Hello\nworld") with { Template = "{Greeting}" }));
        Assert.Equal(new string('a', 60), LogsLinks.SuggestedPattern(Entry(new string('a', 80))));
    }

    [Fact]
    public void ThePreview_KeepsTheFirstLine_CutWithAnEllipsis()
    {
        Assert.Equal("short", LogsFormat.Preview("short"));
        Assert.Equal("first…", LogsFormat.Preview("first\r\nsecond"));
        Assert.Equal(new string('a', LogsFormat.PreviewLength) + "…", LogsFormat.Preview(new string('a', 200)));
    }

    [Theory]
    [InlineData(null, null, null, null, 0L, null)]
    [InlineData(" ", "*.json", "rules.json", 50, 4096L, 5)]
    [InlineData("logs", "../*.json", "rules.json", 50, 4096L, 5)]
    [InlineData("logs", "*.json", "sub/rules.json", 50, 4096L, 5)]
    [InlineData("logs", "*.json", "rules.json", 0, 4096L, 5)]
    [InlineData("logs", "*.json", "rules.json", 1001, 4096L, 5)]
    [InlineData("logs", "*.json", "rules.json", 50, 4095L, 5)]
    [InlineData("logs", "*.json", "rules.json", 50, 1024L * 1024 * 1024 + 1, 5)]
    [InlineData("logs", "*.json", "rules.json", 50, 4096L, 0)]
    [InlineData("logs", "*.json", "rules.json", 50, 4096L, 3601)]
    public void UnusableOptions_AreRefused(string? directory, string? pattern, string? rules, int? pageSize, long maxBytes, int? follow)
    {
        var options = new LogsOptions
        {
            Directory = directory!,
            FilePattern = pattern!,
            RulesFile = rules!,
            PageSize = pageSize ?? 50,
            MaxBytesRead = maxBytes,
            FollowRefreshSeconds = follow ?? 5,
        };

        var result = new LogsOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("OmniLogs:", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaults_WithAFolder_AreAccepted_AndTheirLimitsToo()
    {
        Assert.True(new LogsOptionsValidator().Validate(null, new LogsOptions { Directory = "logs" }).Succeeded);
        Assert.True(new LogsOptionsValidator().Validate(null, new LogsOptions
        {
            Directory = "logs", FilePattern = "app-??.json", PageSize = 1000, MaxBytesRead = LogsOptions.MaxMaxBytesRead, FollowRefreshSeconds = 3600,
        }).Succeeded);
        Assert.True(new LogsOptionsValidator().Validate(null, new LogsOptions
        {
            Directory = "logs", PageSize = 1, MaxBytesRead = LogsOptions.MinMaxBytesRead, FollowRefreshSeconds = 1,
        }).Succeeded);
        Assert.Throws<ArgumentNullException>(() => new LogsOptionsValidator().Validate(null, null!));
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
