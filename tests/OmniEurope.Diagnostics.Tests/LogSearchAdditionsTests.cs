// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The caller's exclusions, plain-text files, several file patterns and the asynchronous reading, over a real
/// folder and the journal a host resolves.</summary>
public sealed class LogSearchAdditionsTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempLogFolder _folder = new();
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ILogJournal Journal(string pattern = LogsOptions.DefaultFilePattern)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniLogs(options =>
        {
            options.Directory = _folder.Path;
            options.FilePattern = pattern;
        });
        var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(provider);
        return provider.CreateScope().ServiceProvider.GetRequiredService<ILogJournal>();
    }

    private void WriteJsonFile()
    {
        _folder.Write("app.json", [
            Clef.Line(Clef.Noon, "Hub call failed", "Error", "Microsoft.AspNetCore.SignalR.HubConnectionHandler"),
            Clef.Line(Clef.Noon.AddMinutes(1), "Health check ok", "Debug", "App.Health"),
            Clef.Line(Clef.Noon.AddMinutes(2), "Order failed", "Error", "Shop.Orders"),
            Clef.Line(Clef.Noon.AddMinutes(3), "Tenant 42 synced", "Information", "Shop.Sync"),
        ], Day);
        _folder.Write("omni-log-rules.json", ["""{"nextId":2,"rules":[{"id":1,"pattern":"health check","enabled":true}]}"""]);
    }

    [Fact]
    public async Task TheCallersExclusions_LeaveEntriesOutOfBothTabsAndBothCounts()
    {
        WriteJsonFile();
        var journal = Journal();

        var result = await journal.SearchAsync(new LogSearch
        {
            Exclusions = [new LogExclusion(Category: "signalr"), new LogExclusion(Message: "TENANT 42"), new LogExclusion()],
        }, Ct);

        Assert.Equal(["Order failed"], result.Entries.Select(row => row.Entry.Message));
        Assert.Equal((1, 1), (result.JournalCount, result.ExcludedCount));
        var both = await journal.SearchAsync(new LogSearch { AllTabs = true, Exclusions = [new LogExclusion(Category: "signalr")] }, Ct);
        Assert.Equal(["Tenant 42 synced", "Order failed", "Health check ok"], both.Entries.Select(row => row.Entry.Message));
    }

    [Theory]
    [InlineData("Shop", "failed", true)]
    [InlineData("Shop", "synced", false)]
    [InlineData("Other", "failed", false)]
    [InlineData(null, "ORDER", true)]
    [InlineData("shop.orders", null, true)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void AnExclusion_MatchesWhenEveryPartItSetsIsContained_CaseIgnored(string? category, string? message, bool matches)
    {
        var entry = new LogEntry(Clef.Noon, LogSeverity.Error, "Order failed", null, "Shop.Orders", null, null, null, []);

        Assert.Equal(matches, new LogExclusion(category, message).Matches(entry));
        Assert.False(new LogExclusion(Category: "Shop").Matches(entry with { Category = null }));
        Assert.Throws<ArgumentNullException>(() => new LogExclusion("x").Matches(null!));
    }

    [Fact]
    public async Task APlainTextFile_IsListedAndSearched_EachLineAnEntry_WithTheTimestampItStartsWith()
    {
        WriteJsonFile();
        _folder.Write("legacy.log", [
            "2026-11-08 11:00:00.000 +01:00 [ERR] Legacy import failed",
            "System.Exception: boom",
            "   at Legacy.Import()",
            "",
            "2026-11-08T13:30:00Z [INF] Legacy import done",
        ], Day.AddDays(-1));
        _folder.Write("other.txt", ["not listed"]);
        var journal = Journal("*.json; *.log");

        var files = await journal.ListFilesAsync(Ct);
        var result = await journal.SearchAsync(new LogSearch { File = "legacy.log" }, Ct);

        Assert.Equal(["app.json", "legacy.log"], files.Select(file => file.Name));
        Assert.Equal(
            ["2026-11-08T13:30:00Z [INF] Legacy import done", "   at Legacy.Import()", "System.Exception: boom", "2026-11-08 11:00:00.000 +01:00 [ERR] Legacy import failed"],
            result.Entries.Select(row => row.Entry.Message));
        Assert.All(result.Entries, row => Assert.Equal(LogSeverity.Information, row.Entry.Level));
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), result.Entries[^1].Entry.Timestamp);
        // A line without its own instant takes the previous line's, so a stack trace stays with its line.
        Assert.Equal(result.Entries[^1].Entry.Timestamp, result.Entries[1].Entry.Timestamp);
        Assert.Equal([5, 3, 2, 1], result.Entries.Select(row => row.Entry.Line ?? 0));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task ATextFileWithoutAnyTimestamp_IsDatedByItsLastWrite()
    {
        _folder.Write("third-party.log", ["started", "stopped"], Day);

        var result = await Journal("*.log").SearchAsync(new LogSearch(), Ct);

        Assert.Equal(["stopped", "started"], result.Entries.Select(row => row.Entry.Message));
        Assert.All(result.Entries, row => Assert.Equal(new DateTimeOffset(Day), row.Entry.Timestamp));
    }

    [Theory]
    [InlineData("2026-11-08 12:00:00 +02:00 msg", "2026-11-08T10:00:00+00:00")]
    [InlineData("2026-11-08 12:00:00 msg", "2026-11-08T12:00:00+00:00")]
    [InlineData("2026-11-08 msg", "2026-11-08T00:00:00+00:00")]
    [InlineData("2026-11-08T12:00:00.5Z", "2026-11-08T12:00:00.5+00:00")]
    [InlineData("2026-13-40 bad date", null)]
    [InlineData("12:00:00 no date", null)]
    [InlineData("2026/11/08 not iso", null)]
    [InlineData("2026-11-0", null)]
    public void ALeadingTimestamp_IsReadOnlyFromAnIsoDate(string line, string? expected) =>
        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), LogTextLine.LeadingTimestamp(line));

    [Fact]
    public async Task TheRulesAreLoadedAsynchronously_AndACancelledSearchIsCancelled()
    {
        WriteJsonFile();
        var journal = Journal();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.SearchAsync(new LogSearch(), cancelled.Token));
        var result = await journal.SearchAsync(new LogSearch(), Ct);
        Assert.Equal([1], result.Rules.Select(rule => rule.Id));
        Assert.Null(result.RulesError);
    }

    [Fact]
    public async Task AnUnreadableRulesFile_IsReported_ByTheAsynchronousLoad()
    {
        WriteJsonFile();
        _folder.Write("omni-log-rules.json", ["{ not json"]);

        var result = await Journal().SearchAsync(new LogSearch(), Ct);

        Assert.NotNull(result.RulesError);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public async Task AnOpenedFile_IsReadAsynchronously()
    {
        WriteJsonFile();

        await using var stream = await Journal().OpenFileAsync("app.json", Ct);

        var file = Assert.IsType<FileStream>(stream);
        // Since .NET 11, IsAsync on Unix reports O_NONBLOCK, which a regular file never has: the property only
        // reflects FileOptions.Asynchronous where file I/O can be asynchronous, on Windows and before .NET 11.
        if (OperatingSystem.IsWindows() || Environment.Version.Major < 11)
        {
            Assert.True(file.IsAsync);
        }

        var buffer = new byte[16];
        Assert.True(await file.ReadAsync(buffer, Ct) > 0);
    }

    [Fact]
    public void TheSharedOpening_AsksForAsynchronousReadsOnlyWhenRequested()
    {
        // Checked on the options themselves: IsAsync no longer reports them on Unix since .NET 11.
        Assert.Equal(FileOptions.Asynchronous, LogFileReader.SharedOptions(asynchronous: true).Options);
        Assert.Equal(FileOptions.None, LogFileReader.SharedOptions(asynchronous: false).Options);
        Assert.Equal(FileShare.ReadWrite | FileShare.Delete, LogFileReader.SharedOptions(asynchronous: true).Share);
    }

    [Fact]
    public void SeveralPatterns_AreValidated_EachAsAFileName()
    {
        var validator = new LogsOptionsValidator();

        Assert.True(validator.Validate(null, new LogsOptions { Directory = "logs", FilePattern = "*.json;*.log" }).Succeeded);
        Assert.False(validator.Validate(null, new LogsOptions { Directory = "logs", FilePattern = "*.json;../*.log" }).Succeeded);
        Assert.False(validator.Validate(null, new LogsOptions { Directory = "logs", FilePattern = " ; " }).Succeeded);
    }

    [Fact]
    public void TheChangeSignal_WatchesEveryPattern()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniLogs(options =>
        {
            options.Directory = _folder.Path;
            options.FilePattern = "*.json;*.log";
        });
        var provider = builder.Services.BuildServiceProvider();
        _providers.Add(provider);
        var changes = (LogChangeWatcher)provider.GetRequiredService<ILogChanges>();
        var raised = 0;
        changes.Changed += (_, _) => raised++;

        changes.OnFileEvent("notes.txt");
        Assert.Equal(0, raised);
        changes.OnFileEvent("legacy.log");
        Assert.Equal(1, raised);
    }
}
