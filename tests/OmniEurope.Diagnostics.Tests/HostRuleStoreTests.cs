// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>A host keeps its rules itself (a database, say): the pages and the journal apply them, whatever the order.</summary>
public sealed class HostRuleStoreTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AHostRuleStore_IsAppliedByThePages_AndByTheJournal(bool registeredBefore, bool scoped)
    {
        _folder.Write("app.json", [Clef.Line(Clef.Noon, "Kept entry"), Clef.Line(Clef.Noon.AddMinutes(1), "Noisy heartbeat")]);
        var store = new MemoryRuleStore();
        store.Rules.Add(new LogRule(7, "heartbeat", null, Enabled: true));
        void Register(IServiceCollection services)
        {
            if (scoped)
                services.AddScoped<ILogRuleStore>(_ => store);
            else
                services.AddSingleton<ILogRuleStore>(store);
        }
        await using var host = await LogsHost.StartAsync(_folder.Path,
            before: registeredBefore ? Register : null, after: registeredBefore ? null : Register);

        var journalPage = await host.GetHtmlAsync("/logs");
        var excludedPage = await host.GetHtmlAsync("/logs?tab=excluded");
        var rulesPage = await host.GetHtmlAsync("/logs/rules");

        Assert.Contains("Kept entry", journalPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Noisy heartbeat", journalPage, StringComparison.Ordinal);
        Assert.Contains("Noisy heartbeat", excludedPage, StringComparison.Ordinal);
        Assert.Contains("heartbeat", rulesPage, StringComparison.Ordinal);

        var added = await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", "Kept"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, added.StatusCode);
        Assert.Equal(["heartbeat", "Kept"], store.Rules.Select(rule => rule.Pattern));
        var disabled = await host.PostWithTokenAsync("/logs/rules", ("action", "disable:7"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.False(store.Rules[0].Enabled);
        Assert.False(File.Exists(_folder.File(LogsOptions.DefaultRulesFile)));

        using var scope = host.App.Services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<ILogJournal>().SearchAsync(new LogSearch { Excluded = true }, Ct);
        var row = Assert.Single(found.Entries);
        Assert.Equal(("Kept entry", "Kept"), (row.Entry.Message, row.Rule!.Pattern));
        Assert.Equal([7, 8], found.Rules.Select(rule => rule.Id));
    }

    /// <summary>Rules kept in memory, as a database store would keep them in a table.</summary>
    private sealed class MemoryRuleStore : ILogRuleStore
    {
        public List<LogRule> Rules { get; } = [];

        public Task<LogRuleSet> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogRuleSet([.. Rules], null));

        public Task<LogRuleUpdate> AddAsync(string pattern, string? category, CancellationToken cancellationToken = default)
        {
            Rules.Add(new LogRule(Rules.Max(rule => rule.Id) + 1, pattern, category, Enabled: true));
            return Task.FromResult(LogRuleUpdate.Saved);
        }

        public Task<LogRuleUpdate> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
        {
            var index = Rules.FindIndex(rule => rule.Id == id);
            if (index < 0)
                return Task.FromResult(LogRuleUpdate.NotFound);
            Rules[index] = Rules[index] with { Enabled = enabled };
            return Task.FromResult(LogRuleUpdate.Saved);
        }

        public Task<LogRuleUpdate> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rules.RemoveAll(rule => rule.Id == id) > 0 ? LogRuleUpdate.Saved : LogRuleUpdate.NotFound);
    }
}
