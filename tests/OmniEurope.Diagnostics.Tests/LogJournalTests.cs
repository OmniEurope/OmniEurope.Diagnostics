// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The public <see cref="ILogJournal"/> over a real folder, resolved the way a host resolves it.</summary>
public sealed class LogJournalTests : IDisposable
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

    private ILogRuleStore Store() => _providers[^1].GetRequiredService<ILogRuleStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ILogJournal Journal(Action<LogsOptions>? configure = null, Action<IServiceCollection>? services = null, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniLogs(options =>
        {
            options.Directory = _folder.Path;
            configure?.Invoke(options);
        });
        if (time is not null)
            builder.Services.AddSingleton(time);
        services?.Invoke(builder.Services);
        var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(provider);
        return provider.CreateScope().ServiceProvider.GetRequiredService<ILogJournal>();
    }

    private void WriteTwoFiles()
    {
        _folder.Write("app-1.json", [
            Clef.Line(Clef.Noon, "Started", "Information", "App.Host"),
            Clef.Line(Clef.Noon.AddMinutes(1), "Health check ok", "Debug", "App.Health"),
        ], Day.AddDays(-1));
        _folder.Write("app-2.json", [
            Clef.Line(Clef.Noon.AddMinutes(2), "Order failed", "Error", "Shop.Orders", "System.Exception: boom", properties: [("CorrelationId", "corr-42")]),
            Clef.Line(Clef.Noon.AddMinutes(3), "Health check ok", "Debug", "App.Health"),
            Clef.Line(Clef.Noon.AddMinutes(4), "Payment slow", "Warning", "Shop.Pay"),
        ], Day);
        _folder.Write("omni-log-rules.json", ["""{"nextId":3,"rules":[{"id":1,"pattern":"health check","enabled":true},{"id":2,"pattern":"Payment","enabled":false}]}"""]);
        _folder.Write("notes.txt", ["not a log"]);
    }

    [Fact]
    public async Task TheFiles_AreTheMatchingOnes_NewestFirst_WithTheirSizeAndDate_NeverTheRulesFile()
    {
        WriteTwoFiles();

        var files = await Journal().ListFilesAsync(Ct);

        Assert.Equal(["app-2.json", "app-1.json"], files.Select(file => file.Name));
        Assert.Equal(new FileInfo(_folder.File("app-2.json")).Length, files[0].Length);
        Assert.Equal(new DateTimeOffset(Day.AddDays(-1)), files[1].LastWriteUtc);
        Assert.Empty(await Journal(o => o.Directory = Path.Combine(_folder.Path, "absent")).ListFilesAsync(Ct));
    }

    [Fact]
    public async Task TheJournalTab_HoldsWhatNoEnabledRuleMatches_NewestFirst_WithBothCounts()
    {
        WriteTwoFiles();

        var result = await Journal().SearchAsync(new LogSearch(), Ct);

        Assert.Equal(["Payment slow", "Order failed", "Started"], result.Entries.Select(row => row.Entry.Message));
        Assert.All(result.Entries, row => Assert.Null(row.Rule));
        Assert.Equal((3, 2), (result.JournalCount, result.ExcludedCount));
        Assert.Equal([1, 2], result.Rules.Select(rule => rule.Id));
        Assert.Null(result.RulesError);
        Assert.False(result.Truncated);
        Assert.Empty(result.Issues);
        Assert.Equal(("app-2.json", 3), (result.Entries[0].Entry.File, result.Entries[0].Entry.Line));
    }

    [Fact]
    public async Task TheExcludedTab_HoldsTheOthers_WithTheRuleThatExcludedThem_AndAllTabsHoldsBoth()
    {
        WriteTwoFiles();
        var journal = Journal();

        var excluded = await journal.SearchAsync(new LogSearch { Excluded = true }, Ct);
        var all = await journal.SearchAsync(new LogSearch { AllTabs = true, Excluded = true }, Ct);

        Assert.Equal([Clef.Noon.AddMinutes(3), Clef.Noon.AddMinutes(1)], excluded.Entries.Select(row => row.Entry.Timestamp));
        Assert.All(excluded.Entries, row => Assert.Equal(1, row.Rule!.Id));
        Assert.Equal(5, all.Entries.Count);
        Assert.Equal(["Payment slow", "Health check ok", "Order failed", "Health check ok", "Started"], all.Entries.Select(row => row.Entry.Message));
        Assert.Equal((3, 2), (all.JournalCount, all.ExcludedCount));
    }

    [Fact]
    public async Task TheFilters_KeepTheLevelAndAbove_TheDatesIncluded_AndTheTextIgnoringCase()
    {
        WriteTwoFiles();
        var journal = Journal();

        async Task<IEnumerable<string>> Messages(LogSearch search) =>
            (await journal.SearchAsync(search, Ct)).Entries.Select(row => row.Entry.Message);

        Assert.Equal(["Payment slow", "Order failed"], await Messages(new LogSearch { MinLevel = LogSeverity.Warning }));
        Assert.Equal(["Order failed"], await Messages(new LogSearch { From = Clef.Noon.AddMinutes(2), To = Clef.Noon.AddMinutes(3) }));
        Assert.Equal(["Order failed"], await Messages(new LogSearch { Text = "CORR-42" }));
        Assert.Equal(["Order failed"], await Messages(new LogSearch { Text = "boom" }));
        Assert.Equal(["Payment slow", "Order failed"], await Messages(new LogSearch { Text = "shop." }));
        Assert.Equal(["Started"], await Messages(new LogSearch { Text = "start" }));
    }

    [Theory]
    [InlineData("APP-1.JSON", 1)]
    [InlineData("other.json", 0)]
    [InlineData("../app-1.json", 0)]
    [InlineData("notes.txt", 0)]
    [InlineData("omni-log-rules.json", 0)]
    [InlineData("", 3)]
    public async Task OneFile_IsReadAlone_AndANameOutsideTheListingReadsNothing(string file, int expected)
    {
        WriteTwoFiles();

        var result = await Journal().SearchAsync(new LogSearch { File = file }, Ct);

        Assert.Equal(expected, result.Entries.Count);
    }

    [Fact]
    public async Task TheCap_Truncation_TheUnreadableLines_AndAnUnreadableRulesFile_AreReported()
    {
        _folder.Write("app.json", [Clef.Line(Clef.Noon, "kept"), "garbage", Clef.Line(Clef.Noon, new string('x', 6000))]);
        _folder.Write("omni-log-rules.json", ["{ not json"]);

        var result = await Journal(o => o.MaxBytesRead = LogsOptions.MinMaxBytesRead).SearchAsync(new LogSearch(), Ct);
        var full = await Journal().SearchAsync(new LogSearch(), Ct);

        Assert.True(result.Truncated);
        Assert.NotNull(result.RulesError);
        Assert.Empty(result.Rules);
        var issue = Assert.Single(full.Issues);
        Assert.Equal(("app.json", 1), (issue.File, issue.UnreadableCount));
        Assert.Equal([2], issue.Lines);
    }

    [Fact]
    public async Task ACancelledToken_CancelsEveryCall()
    {
        WriteTwoFiles();
        var journal = Journal();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ListFilesAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.SearchAsync(new LogSearch(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.OpenFileAsync("app-1.json", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.PurgeAsync(0, cancelled.Token));
        Assert.Equal(2, Directory.GetFiles(_folder.Path, "app-*.json").Length);
    }

    [Fact]
    public async Task TheToken_ReachesTheReadingLoop()
    {
        WriteTwoFiles();
        using var source = new CancellationTokenSource();
        // A store that cancels while the rules load: the files are read afterwards, so only the reading loop can stop.
        var journal = Journal(services: services => services.AddSingleton<ILogRuleStore>(new CancellingStore(source)));

        var task = journal.SearchAsync(new LogSearch(), source.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    [Fact]
    public async Task AListedFile_OpensSharedForTheWriter_AndAnythingElseIsNull()
    {
        WriteTwoFiles();
        var journal = Journal();

        await using (var stream = await journal.OpenFileAsync("APP-1.json", Ct))
        {
            Assert.NotNull(stream);
            // The host's writer keeps appending while the file is open.
            await File.AppendAllTextAsync(_folder.File("app-1.json"), "more\n", Ct);
            using var reader = new StreamReader(stream);
            Assert.Contains("Started", await reader.ReadToEndAsync(Ct), StringComparison.Ordinal);
        }
        foreach (var name in new[] { "../app-1.json", "notes.txt", "omni-log-rules.json", "gone.json", "", "C:\\app-1.json" })
            Assert.Null(await journal.OpenFileAsync(name, Ct));
    }

    [Fact]
    public async Task AFileLockedWithoutSharing_FaultsTheOpening()
    {
        _folder.Write("locked.json", [Clef.Line(Clef.Noon, "x")]);
        var journal = Journal();
        using var exclusive = new FileStream(_folder.File("locked.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Windows refuses to share a file opened without sharing; on Unix .NET emulates FileShare.None with an
        // advisory lock that its own openings honour, so the opening faults on every platform.
        await Assert.ThrowsAsync<IOException>(() => journal.OpenFileAsync("locked.json", Ct));
    }

    [Fact]
    public async Task ThePurge_DeletesOnlyOldLogFiles_ByTheHostClock_AndRefusesANegativeAge()
    {
        WriteTwoFiles();
        File.SetLastWriteTimeUtc(_folder.File("notes.txt"), Day.AddDays(-90));
        File.SetLastWriteTimeUtc(_folder.File("omni-log-rules.json"), Day.AddDays(-90));
        var journal = Journal(time: new FixedTime(new DateTimeOffset(Day.AddHours(12))));

        Assert.Equal(new LogPurgeResult(0, 0), await journal.PurgeAsync(int.MaxValue, Ct));
        Assert.Equal(new LogPurgeResult(1, 0), await journal.PurgeAsync(1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.PurgeAsync(-1, Ct));

        Assert.Equal(["app-2.json", "notes.txt", "omni-log-rules.json"], Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task TheRuleStore_IsTheDefaultFile_AndItsAsyncMethodsChangeIt()
    {
        var journal = Journal();
        var store = Store();

        Assert.Equal(LogRuleUpdate.Saved, await store.AddAsync("noise", null, Ct));
        Assert.Equal(LogRuleUpdate.Saved, await store.SetEnabledAsync(1, enabled: false, Ct));
        Assert.Equal(LogRuleUpdate.NotFound, await store.DeleteAsync(9, Ct));
        Assert.Equal([new LogRule(1, "noise", null, Enabled: false)], (await store.LoadAsync(Ct)).Rules);
        Assert.Equal(LogRuleUpdate.Saved, await store.DeleteAsync(1, Ct));
        Assert.Empty((await journal.SearchAsync(new LogSearch(), Ct)).Rules);
        Assert.True(File.Exists(_folder.File("omni-log-rules.json")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddAsync("x", null, cancelled.Token));
    }

    [Fact]
    public async Task AFailedRuleWrite_IsReported_AndLeavesNoTemporaryFile()
    {
        // The rules file's place is taken by a folder: the move over it fails.
        Directory.CreateDirectory(_folder.File("omni-log-rules.json"));
        Journal();

        Assert.Equal(LogRuleUpdate.WriteFailed, await Store().AddAsync("x", null, Ct));
        Assert.Empty(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public async Task AWorkThatThrows_FaultsTheTask_RatherThanThrowingFromTheCall()
    {
        var task = SyncTask.Run<int>(() => throw new IOException("disk"), Ct);

        Assert.True(task.IsFaulted);
        await Assert.ThrowsAsync<IOException>(() => task);
    }

    [Fact]
    public void TheRuleMatch_RefusesANullEntry() =>
        Assert.Throws<ArgumentNullException>(() => new LogRule(1, "x", null, true).Matches(null!));

    private sealed class CancellingStore(CancellationTokenSource source) : ILogRuleStore
    {
        public async Task<LogRuleSet> LoadAsync(CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
            return new LogRuleSet([], null);
        }

        public Task<LogRuleUpdate> AddAsync(string pattern, string? category, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LogRuleUpdate> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LogRuleUpdate> DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
