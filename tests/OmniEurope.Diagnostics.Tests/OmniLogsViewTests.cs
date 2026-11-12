// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using OmniEurope.Diagnostics.Logs;
using static OmniEurope.Diagnostics.Tests.OmniLogsViewHarness;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The embeddable logs view over real files: the tabs and their counts, the filters, the pages, the details and
/// the notices, in an interactive render and in a static one.
/// </summary>
public sealed class OmniLogsViewTests : IDisposable
{
    private readonly OmniLogsViewHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task TheJournal_ListsTheEntriesNewestFirst_WithTheCountsOfBothTabs_AndNoStyleOrScript()
    {
        _harness.SeedApp();
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);

        var view = _harness.Render();

        Assert.Equal(["Journal (3)", "Lignes exclues (1)", "Règles d'exclusion (1)"],
            view.FindAll("nav button, nav strong").Select(tab => tab.TextContent));
        Assert.Equal("Journal (3)", view.Find("nav strong[aria-current=page]").TextContent);
        Assert.Equal(["Failed", "Slow query 900 ms", "Started"], Messages(view));
        Assert.Equal("Erreur", view.Find("section tbody tr strong").TextContent);
        Assert.Equal("req-1", EntryRows(view)[0].Children[4].TextContent);
        Assert.Contains("08/11/2026 12:00:02,000", EntryRows(view)[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(view.FindAll("html, head, body, main, h1, [class], [style], style, link, script, a[href]"));
    }

    [Fact]
    public async Task TheExcludedTab_ListsTheLinesAnActiveRuleKeepsOut_WithTheRule()
    {
        _harness.SeedApp();
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);
        var view = _harness.Render();

        Press(view, "Lignes exclues (1)");

        Eventually(view, () => Assert.Equal(["Heartbeat"], Messages(view)));
        Assert.Equal("Heartbeat", EntryRows(view)[0].Children[4].TextContent);
        Assert.Contains("Lignes écartées du journal par une règle active.", view.Markup, StringComparison.Ordinal);
        Assert.False(HasButton(view, "Exclure…"));

        Press(view, "Journal (3)");
        Eventually(view, () => Assert.Equal(["Failed", "Slow query 900 ms", "Started"], Messages(view)));
    }

    [Fact]
    public void TheMinimumLevel_TheText_AndTheFile_FilterTheEntries_AndTheResetBringsThemBack()
    {
        _harness.SeedApp();
        _harness.Folder.Write("other.json", [Clef.Line(Clef.Noon.AddSeconds(4), "Other file", "Warning", "Misc")]);
        var view = _harness.Render();
        Assert.Equal(5, EntryRows(view).Count);

        view.Find("#omni-logs-level").Change("Warning");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal(["Other file", "Failed", "Slow query 900 ms"], Messages(view)));

        view.Find("#omni-logs-text").Change("  DB ");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal(["Failed", "Slow query 900 ms"], Messages(view)));

        view.Find("#omni-logs-file").Change("other.json");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal("Aucune entrée ne correspond aux filtres.", view.Find("section p").TextContent));

        Press(view, "Effacer les filtres");
        Eventually(view, () =>
        {
            Assert.Equal(5, EntryRows(view).Count);
            Assert.Equal("", view.Find("#omni-logs-text").GetAttribute("value"));
        });
    }

    [Fact]
    public void TheDates_AreReadAsUtc_FromTheMinuteToTheFraction_AndAnInvalidOneIsShown()
    {
        _harness.SeedApp();
        var view = _harness.Render();

        view.Find("#omni-logs-from").Change("2026-11-08T12:00:01");
        view.Find("#omni-logs-to").Change("2026-11-08T12:00:02.000");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal(["Failed", "Slow query 900 ms"], Messages(view)));

        view.Find("#omni-logs-from").Change("2026-11-08T12:01");
        view.Find("#omni-logs-to").Change("");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal("Aucune entrée ne correspond aux filtres.", view.Find("section p").TextContent));

        view.Find("#omni-logs-from").Change("yesterday");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal("La date saisie n'est pas valide (année-mois-jour heure:minute, en UTC).", view.Find("p[role=alert]").TextContent));
    }

    [Fact]
    public void TheEntries_ArePaged_ByThePageSizeOfTheOptions()
    {
        using var paged = new OmniLogsViewHarness(options => options.PageSize = 2);
        paged.SeedApp();
        var view = paged.Render();

        Assert.Equal(["Heartbeat", "Failed"], Messages(view));
        Assert.Contains("Lignes 1 à 2 sur 4", view.Find("section p").TextContent, StringComparison.Ordinal);
        Assert.Contains("Page 1 sur 2", view.Find("section nav p").TextContent, StringComparison.Ordinal);
        Assert.False(HasButton(view, "Précédent"));

        Press(view, "Suivant");
        Eventually(view, () => Assert.Equal(["Slow query 900 ms", "Started"], Messages(view)));
        Assert.Contains("Lignes 3 à 4 sur 4", view.Find("section p").TextContent, StringComparison.Ordinal);
        Assert.False(HasButton(view, "Suivant"));

        Press(view, "Précédent");
        Eventually(view, () => Assert.Equal(["Heartbeat", "Failed"], Messages(view)));
    }

    [Fact]
    public void ADetail_OpensInPlace_WithTheMessage_TheException_AndTheProperties_AndClosesAgain()
    {
        _harness.SeedApp();
        var view = _harness.Render();
        var failed = EntryRows(view).Single(row => row.Children[3].TextContent == "Failed");
        Assert.Equal("false", failed.QuerySelector("button")!.GetAttribute("aria-expanded"));

        failed.QuerySelector("button")!.Click();

        Eventually(view, () =>
        {
            var detail = view.Find("section table > tbody").Children.Single(row => row.Children.Length == 1);
            Assert.Equal("Failed", detail.QuerySelector("pre")!.TextContent);
            Assert.Equal("System.InvalidOperationException: boom", detail.QuerySelector("pre:nth-of-type(2)")!.TextContent);
            Assert.Equal(["Table", "Orders"], detail.QuerySelectorAll("td table td").Select(cell => cell.TextContent));
            Assert.Contains("app.json, ligne 3", detail.QuerySelector("dl")!.TextContent, StringComparison.Ordinal);
            Assert.Equal("true", EntryRows(view).Single(row => row.Children[3].TextContent == "Failed").QuerySelector("button")!.GetAttribute("aria-expanded"));
        });

        Press(view, "Masquer");
        Eventually(view, () => Assert.DoesNotContain(view.Find("section table > tbody").Children, row => row.Children.Length == 1));
    }

    [Fact]
    public void TheSameRequest_FiltersOnTheCorrelationId_AndLeavesTheOtherFilters()
    {
        _harness.SeedApp();
        var view = _harness.Render();
        view.Find("#omni-logs-level").Change("Error");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal(["Failed"], Messages(view)));
        EntryRows(view)[0].QuerySelector("button")!.Click();

        Eventually(view, () => Assert.True(HasButton(view, "toutes les entrées de cette requête")));
        Press(view, "toutes les entrées de cette requête");

        Eventually(view, () =>
        {
            Assert.Equal(["Failed", "Slow query 900 ms"], Messages(view));
            Assert.Equal("req-1", view.Find("#omni-logs-text").GetAttribute("value"));
            Assert.Null(view.Find("#omni-logs-level").QuerySelector("option[selected]"));
        });
    }

    [Fact]
    public void ARefresh_ReadsTheFilesAgain_AndTheNoticesSayWhatWasLeftOut()
    {
        using var capped = new OmniLogsViewHarness(options => options.MaxBytesRead = LogsOptions.MinMaxBytesRead);
        capped.Folder.Write("app.json", Enumerable.Range(0, 60).Select(index => Clef.Line(Clef.Noon.AddSeconds(index), "Line " + index + new string('x', 100), "Information", "Cat"))
            .Append("not json at all"));
        var view = capped.Render();

        Assert.Contains("Lecture plafonnée à 4 Ko", view.Markup, StringComparison.Ordinal);
        Assert.Contains("n'a pas pu être lu : 1 ligne illisible ignorée", view.Markup, StringComparison.Ordinal);

        capped.Folder.Write("app.json", [Clef.Line(Clef.Noon, "Only one now", "Information", "Cat")]);
        Press(view, "Actualiser");

        Eventually(view, () =>
        {
            Assert.Equal(["Only one now"], Messages(view));
            Assert.DoesNotContain("Lecture plafonnée", view.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnEmptyFolder_AndUnreadableRules_AreExplained()
    {
        File.WriteAllText(_harness.Folder.File("omni-log-rules.json"), "{ not json");
        var view = _harness.Render();

        Assert.Contains("Aucun fichier de log trouvé dans", view.Markup, StringComparison.Ordinal);
        Assert.Contains("est illisible : aucune règle n'est appliquée.", view.Markup, StringComparison.Ordinal);
        Assert.Equal("Aucune entrée ne correspond aux filtres.", view.Find("section p").TextContent);
    }

    [Fact]
    public void StaticRendering_ShowsTheFirstPageOfTheJournal_WithoutAnyControl()
    {
        using var rendered = new OmniLogsViewHarness(interactive: false);
        rendered.SeedApp();

        var view = rendered.Render();

        Assert.Equal(["Heartbeat", "Failed", "Slow query 900 ms", "Started"], Messages(view));
        Assert.Empty(view.FindAll("button, input, select, form, nav"));
        Assert.Equal(5, view.FindAll("thead th").Count);
    }

    [Fact]
    public void English_UsesTheEnglishTexts()
    {
        _harness.UseCulture("en-GB");
        _harness.SeedApp();

        var view = _harness.Render();

        Assert.Equal(["Journal (4)", "Excluded lines (0)", "Exclusion rules (0)"], view.FindAll("nav button, nav strong").Select(tab => tab.TextContent));
        Assert.True(HasButton(view, "Refresh"));
        Assert.Equal("Warning", EntryRows(view)[2].Children[1].TextContent);
        Assert.Contains("Updated: read at", view.Markup, StringComparison.Ordinal);
    }
}
