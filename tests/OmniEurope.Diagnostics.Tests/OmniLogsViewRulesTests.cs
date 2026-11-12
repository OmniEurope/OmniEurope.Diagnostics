// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Components;
using OmniEurope.Diagnostics.Logs;
using static OmniEurope.Diagnostics.Tests.OmniLogsViewHarness;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The exclusion rules and the purge of the logs view, through the real rule store and the real files.</summary>
public sealed class OmniLogsViewRulesTests : IDisposable
{
    private static readonly DateTimeOffset Now = Clef.Noon;
    private readonly OmniLogsViewHarness _harness = new(time: new FixedTime(Now));

    public void Dispose() => _harness.Dispose();

    private static void OpenRules(IRenderedComponent<OmniLogsView> view)
    {
        view.FindAll("nav button").Single(button => button.TextContent.StartsWith("Règles d'exclusion", StringComparison.Ordinal)).Click();
        Eventually(view, () => Assert.NotEmpty(view.FindAll("#omni-logs-pattern")));
    }

    private static string Status(IRenderedComponent<OmniLogsView> view) => view.Find("p[role=status]").TextContent;

    private static string Alert(IRenderedComponent<OmniLogsView> view) => view.Find("p[role=alert]").TextContent;

    private async Task<LogRuleSet> StoredAsync() => await _harness.RuleStore.LoadAsync(Xunit.TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARuleIsAdded_ThroughTheStore_ListedAndAppliedToTheTabs()
    {
        _harness.SeedApp();
        var view = _harness.Render();
        OpenRules(view);
        Assert.Contains("Aucune règle pour l'instant.", view.Markup, StringComparison.Ordinal);

        view.Find("#omni-logs-pattern").Change("  Heartbeat ");
        view.Find("#omni-logs-category").Change("Health");
        view.Find("form").Submit();

        Eventually(view, () => Assert.Equal("Règle ajoutée.", Status(view)));
        var stored = (await StoredAsync()).Rules.Single();
        Assert.Equal(("Heartbeat", "Health", true), (stored.Pattern, stored.Category, stored.Enabled));
        Eventually(view, () =>
        {
            Assert.Equal(["Heartbeat", "Health", "Active"], view.FindAll("section table tbody td").Take(3).Select(cell => cell.TextContent));
            Assert.Equal(("", ""), (view.Find("#omni-logs-pattern").GetAttribute("value") ?? "", view.Find("#omni-logs-category").GetAttribute("value") ?? ""));
            Assert.Contains(view.FindAll("nav button"), tab => tab.TextContent == "Journal (3)");
            Assert.Contains(view.FindAll("nav button"), tab => tab.TextContent == "Lignes exclues (1)");
        });
    }

    [Fact]
    public async Task ARuleWithoutText_OrTooLong_IsRefused_AndNothingIsStored()
    {
        _harness.SeedApp();
        var view = _harness.Render();
        OpenRules(view);

        view.Find("#omni-logs-pattern").Change("   ");
        view.Find("form").Submit();
        Eventually(view, () => Assert.StartsWith("Le texte du message est obligatoire", Alert(view), StringComparison.Ordinal));

        view.Find("#omni-logs-pattern").Change(new string('a', LogRule.MaxLength + 1));
        view.Find("form").Submit();
        Eventually(view, () => Assert.StartsWith("Le texte du message est obligatoire", Alert(view), StringComparison.Ordinal));

        view.Find("#omni-logs-pattern").Change("ok");
        view.Find("#omni-logs-category").Change(new string('c', LogRule.MaxLength + 1));
        view.Find("form").Submit();
        Eventually(view, () => Assert.StartsWith("Le texte du message est obligatoire", Alert(view), StringComparison.Ordinal));
        Assert.Empty((await StoredAsync()).Rules);
    }

    [Fact]
    public async Task ARule_IsDisabledAndEnabledAgain_AndTheTabsFollow()
    {
        _harness.SeedApp();
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);
        var view = _harness.Render();
        OpenRules(view);
        Assert.Equal(["Heartbeat", "(toutes)", "Active"], view.FindAll("section table tbody td").Take(3).Select(cell => cell.TextContent));

        Press(view, "Désactiver");

        Eventually(view, () => Assert.Equal("Règle désactivée.", Status(view)));
        Assert.False((await StoredAsync()).Rules.Single().Enabled);
        Eventually(view, () =>
        {
            Assert.Equal("Désactivée", view.FindAll("section table tbody td")[2].TextContent);
            Assert.Contains(view.FindAll("nav button"), tab => tab.TextContent == "Lignes exclues (0)");
        });

        Press(view, "Activer");

        Eventually(view, () => Assert.Equal("Règle activée.", Status(view)));
        Assert.True((await StoredAsync()).Rules.Single().Enabled);
        Eventually(view, () => Assert.Contains(view.FindAll("nav button"), tab => tab.TextContent == "Lignes exclues (1)"));
    }

    [Fact]
    public async Task ARule_IsDeleted()
    {
        _harness.SeedApp();
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);
        var view = _harness.Render();
        OpenRules(view);

        Press(view, "Supprimer");

        Eventually(view, () => Assert.Equal("Règle supprimée.", Status(view)));
        Assert.Empty((await StoredAsync()).Rules);
        Eventually(view, () => Assert.Contains("Aucune règle pour l'instant.", view.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void Exclude_OpensTheRulesWithTheRuleOfTheEntryFilledIn()
    {
        _harness.SeedApp();
        var view = _harness.Render();

        EntryRows(view).Single(row => row.Children[3].TextContent == "Slow query 900 ms").QuerySelectorAll("button")[1].Click();

        Eventually(view, () =>
        {
            Assert.Equal("Slow query", view.Find("#omni-logs-pattern").GetAttribute("value"));
            Assert.Equal("Db", view.Find("#omni-logs-category").GetAttribute("value"));
        });
    }

    [Fact]
    public async Task ARuleFileThatCannotBeRead_IsReportedByTheStore_AndNothingIsWritten()
    {
        _harness.SeedApp();
        File.WriteAllText(_harness.Folder.File("omni-log-rules.json"), "{ not json");
        var view = _harness.Render();
        OpenRules(view);

        view.Find("#omni-logs-pattern").Change("Heartbeat");
        view.Find("form").Submit();

        Eventually(view, () => Assert.Contains("est illisible : aucune règle n'est appliquée.", Alert(view), StringComparison.Ordinal));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(_harness.Folder.File("omni-log-rules.json"), Xunit.TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(LogRuleUpdate.NotFound, "Cette règle n'existe plus.")]
    [InlineData(LogRuleUpdate.WriteFailed, "Le fichier de règles n'a pas pu être enregistré")]
    public async Task AnUpdateTheStoreRefuses_IsShown(LogRuleUpdate answer, string text)
    {
        _harness.SeedApp();
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);
        _harness.Context.Services.AddSingleton<ILogRuleStore>(new RuleStoreDouble(_harness.RuleStore) { Answer = answer });
        var view = _harness.Render();
        OpenRules(view);

        Press(view, "Supprimer");

        Eventually(view, () => Assert.StartsWith(text, Alert(view), StringComparison.Ordinal));
        Assert.Single((await StoredAsync()).Rules);
    }

    [Fact]
    public void AStoreThatThrows_IsShownAsText_NotThrownOutOfTheView()
    {
        _harness.SeedApp();
        _harness.Context.Services.AddSingleton<ILogRuleStore>(new RuleStoreDouble(_harness.RuleStore) { Failure = new InvalidOperationException("the database is gone") });
        var view = _harness.Render();
        OpenRules(view);

        view.Find("#omni-logs-pattern").Change("Heartbeat");
        view.Find("form").Submit();

        Eventually(view, () => Assert.Equal("L'opération a échoué : the database is gone", Alert(view)));
        Assert.Equal("Heartbeat", view.Find("#omni-logs-pattern").GetAttribute("value"));
    }

    [Fact]
    public void ThePurge_AsksFirst_CanBeCancelled_ThenDeletesTheOldFilesOnly()
    {
        _harness.Folder.Write("old.json", [Clef.Line(Now.AddDays(-10), "Old", "Information", "Cat")], Now.AddDays(-10).UtcDateTime);
        _harness.Folder.Write("fresh.json", [Clef.Line(Now, "Fresh", "Information", "Cat")], Now.AddHours(-1).UtcDateTime);
        var view = _harness.Render();
        OpenRules(view);
        view.Find("#omni-logs-days").Change("5");

        Press(view, "Purger");

        Eventually(view, () => Assert.StartsWith("Supprimer définitivement 1 fichiers (1 Ko) datant de plus de 5 jours ?", Alert(view), StringComparison.Ordinal));
        Assert.True(File.Exists(_harness.Folder.File("old.json")));
        Assert.NotNull(view.Find("#omni-logs-days").GetAttribute("disabled"));
        Assert.False(HasButton(view, "Purger"));

        Press(view, "Annuler");
        Eventually(view, () => Assert.True(HasButton(view, "Purger")));
        Assert.True(File.Exists(_harness.Folder.File("old.json")));
        Assert.Empty(view.FindAll("p[role=alert]"));

        Press(view, "Purger");
        Eventually(view, () => Assert.True(HasButton(view, "Confirmer la suppression")));
        Press(view, "Confirmer la suppression");

        Eventually(view, () => Assert.Equal("Purge effectuée : 1 fichiers supprimés.", Status(view)));
        Assert.False(File.Exists(_harness.Folder.File("old.json")));
        Assert.True(File.Exists(_harness.Folder.File("fresh.json")));
        Assert.Empty(view.FindAll("p[role=alert]"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("36501")]
    public void APurgeOverTheRange_IsRefused_WithoutAsking(string days)
    {
        var view = _harness.Render();
        OpenRules(view);

        view.Find("#omni-logs-days").Change(days);
        Press(view, "Purger");

        Eventually(view, () => Assert.Equal("Le nombre de jours doit être un entier d'au moins 1.", Alert(view)));
        Assert.False(HasButton(view, "Confirmer la suppression"));
    }

    [Fact]
    public void AFileThatCannotBeDeleted_IsReportedAfterThePurge()
    {
        var journal = new JournalDouble(_harness.Journal()) { OnPurge = _ => Task.FromResult(new LogPurgeResult(1, 2)) };
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);
        var view = _harness.Render();
        OpenRules(view);

        Press(view, "Purger");
        Eventually(view, () => Assert.True(HasButton(view, "Confirmer la suppression")));
        Press(view, "Confirmer la suppression");

        Eventually(view, () =>
        {
            Assert.Equal("Purge effectuée : 1 fichiers supprimés.", Status(view));
            Assert.Equal("2 fichiers n'ont pas pu être supprimés (encore ouverts ou en lecture seule).", Alert(view));
        });
    }

    [Fact]
    public void ThePurgeOfTheStore_ThatThrows_IsShownAsText()
    {
        var journal = new JournalDouble(_harness.Journal()) { OnPurge = _ => throw new IOException("disk on fire") };
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);
        var view = _harness.Render();
        OpenRules(view);

        Press(view, "Purger");
        Eventually(view, () => Assert.True(HasButton(view, "Confirmer la suppression")));
        Press(view, "Confirmer la suppression");

        Eventually(view, () => Assert.Equal("L'opération a échoué : disk on fire", Alert(view)));
    }
}
