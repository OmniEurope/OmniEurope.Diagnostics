// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Components;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The logs pages as they render a model built by hand: the notices, the states and the texts.</summary>
public sealed class LogsPageTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly BunitContext _context = new();
    private readonly LogsPageState _state = new();

    public LogsPageTests()
    {
        _context.Services.AddLocalization();
        _context.Services.AddSingleton(_state);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
    }

    public void Dispose()
    {
        _context.Dispose();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    private static LogsJournalModel Journal(IReadOnlyList<LogRow> rows, IReadOnlyList<LogFileIssue>? issues = null, LogQuery? query = null) => new()
    {
        Links = new LogsLinks("/logs"),
        Query = query ?? new LogQuery(),
        Files = ["app.json"],
        Source = "logs/*.json",
        DirectoryLabel = "logs/",
        RulesLabel = "logs/omni-log-rules.json",
        ReadAt = Clef.Noon,
        JournalCount = rows.Count,
        ExcludedCount = 0,
        RuleCount = 0,
        Rows = rows,
        Page = 1,
        PageCount = 1,
        Offset = 0,
        Issues = issues ?? [],
        Truncated = false,
        MaxBytesRead = LogsOptions.DefaultMaxBytesRead,
        RulesError = null,
        FollowSeconds = 5,
    };

    private static LogRulesModel Rules(IReadOnlyList<LogRuleCount> rules, string? done = null, LogPurgeResult? purged = null) => new()
    {
        Links = new LogsLinks("/logs"),
        RulesLabel = "logs/omni-log-rules.json",
        TokenField = "__RequestVerificationToken",
        Token = "token-value",
        Pattern = null,
        Category = null,
        PrefillCount = null,
        Rules = rules,
        RulesError = null,
        PurgeDays = 30,
        PurgeCount = 4,
        PurgeBytes = 128L * 1024 * 1024,
        Done = done,
        Purged = purged,
        Truncated = false,
    };

    [Fact]
    public void EachUnreadableFile_IsNamed_WithItsCountAndLines()
    {
        var failed = new LogFileIssue("gone.json") { Failed = true };
        var many = new LogFileIssue("app-20261105.json") { UnreadableCount = 3, Lines = [1022, 1023, 4870] };
        var unnumbered = new LogFileIssue("cut.json") { UnreadableCount = 2 };
        _state.Journal = Journal([], [failed, many, unnumbered]);

        var paragraphs = _context.Render<LogsPage>().FindAll("p").Select(p => p.TextContent.Trim().Replace(' ', ' ').Replace(' ', ' ')).ToList();

        Assert.Contains("Le fichier gone.json n'a pas pu être lu.", paragraphs);
        Assert.Contains("Le fichier app-20261105.json n'a pas pu être lu : 3 lignes illisibles ignorées (ligne 1 022, 1 023, 4 870).", paragraphs);
        Assert.Contains("Le fichier cut.json n'a pas pu être lu : 2 lignes illisibles ignorées.", paragraphs);
    }

    [Fact]
    public void AnEntryWithoutCategoryCorrelationOrLine_ShowsWhatItHas_AndNoClassOrStyle()
    {
        var entry = new LogEntry(Clef.Noon, LogSeverity.Information, "Started", null, null, null, null, null, []) { File = "app.json" };
        _state.Journal = Journal([new LogRow(entry, null)]);

        var page = _context.Render<LogsPage>();

        var cells = page.Find("tbody tr").QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToArray();
        Assert.Equal(["08/11/2026 12:00:00,000", "Information", "", "Started", "", "Voir | Exclure…"], cells.Select(c => System.Text.RegularExpressions.Regex.Replace(c, @"\s+", " ")));
        Assert.Equal(["Date", "Niveau", "Fichier"], page.FindAll("details dt").Select(dt => dt.TextContent));
        Assert.Equal("app.json", page.Find("details dd code").TextContent);
        Assert.Empty(page.FindAll("details h3 + table"));
        Assert.Empty(page.FindAll("[class], [style], style, link, script"));
    }

    [Theory]
    [InlineData("fr-FR", new[] { "Détail", "Débogage", "Information", "Avertissement", "Erreur", "Fatale" })]
    [InlineData("en-GB", new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" })]
    public void EveryLevel_IsShownLocalized_InTheFilterTheTableAndTheDetail_WhileFormValuesStayTechnical(string culture, string[] labels)
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var levels = Enum.GetValues<LogSeverity>();
        var rows = levels.Select(level => new LogRow(new LogEntry(Clef.Noon, level, "m", null, null, null, null, null, []) { File = "app.json" }, null)).ToList();
        _state.Journal = Journal(rows);

        var page = _context.Render<LogsPage>();

        var options = page.FindAll("select[name=level] option").Skip(1).ToList();
        Assert.Equal(labels, options.Select(o => o.TextContent));
        Assert.Equal(levels.Select(l => l.ToString()), options.Select(o => o.GetAttribute("value")));
        Assert.Equal(labels, page.FindAll("tbody tr").Select(tr => tr.QuerySelectorAll("td")[1].TextContent.Trim()));
        Assert.Equal(labels, page.FindAll("details dl dd").Where((_, i) => i % 3 == 1).Select(dd => dd.TextContent));
        Assert.Equal(labels.Select(l => l), page.FindAll("details summary").Select(s => s.TextContent.Split(", ")[1]));
        Assert.Empty(page.FindAll("[class], [style], style, link, script"));
    }

    [Fact]
    public void TheRulesPage_ShowsEveryRuleState_AndCarriesTheTokenInEachForm()
    {
        _state.Rules = Rules([
            new LogRuleCount(new LogRule(1, "No XML encryptor configured", null, Enabled: true), 24),
            new LogRuleCount(new LogRule(3, "Request starting", "Microsoft.AspNetCore.Hosting.Diagnostics", Enabled: false), 0),
        ]);

        var page = _context.Render<LogRulesPage>();

        var rows = page.FindAll("tbody tr").Select(tr => tr.QuerySelectorAll("td").Take(4).Select(td => td.TextContent).ToArray()).ToArray();
        Assert.Equal(["No XML encryptor configured", "(toutes)", "24", "Active"], rows[0]);
        Assert.Equal(["Request starting", "Microsoft.AspNetCore.Hosting.Diagnostics", "0", "Désactivée"], rows[1]);
        Assert.Equal(["add", "disable:1", "delete:1", "enable:3", "delete:3", "purge"], page.FindAll("button").Select(b => b.GetAttribute("value")));
        Assert.Equal(4, page.FindAll("input[type=hidden][name=__RequestVerificationToken][value=token-value]").Count);
        Assert.Equal("Je confirme : 4 fichiers (128 Mo) de plus de 30 jours seront supprimés définitivement.", page.Find("label[for=confirm]").TextContent);
        Assert.Empty(page.FindAll("[role=status], [role=alert]"));
        Assert.Empty(page.FindAll("[class], [style], style, link, script"));
    }

    [Fact]
    public void AnUnknownConfirmation_IsNotShown_AndAPartialPurgeSaysWhatRemains()
    {
        _state.Rules = Rules([], done: "hacked", purged: new LogPurgeResult(2, 1));

        var page = _context.Render<LogRulesPage>();

        Assert.Equal(["Purge effectuée : 2 fichiers supprimés."], page.FindAll("[role=status]").Select(p => p.TextContent));
        Assert.Equal("1 fichiers n'ont pas pu être supprimés (encore ouverts ou en lecture seule).", page.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("Done_", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordsAroundTheFollowLink_AndBetweenTheRuleButtons_AreSeparated()
    {
        _state.Journal = Journal([]);
        _state.Rules = Rules([new LogRuleCount(new LogRule(1, "No XML encryptor configured", null, Enabled: true), 24)]);

        var update = _context.Render<LogsPage>().FindAll("p").Select(p => p.TextContent).Single(text => text.Contains("Suivre", StringComparison.Ordinal));
        var rules = _context.Render<LogRulesPage>().Markup;

        Assert.Matches(@"\. Suivre en direct \(", System.Text.RegularExpressions.Regex.Replace(update, @"\s+", " "));
        Assert.Matches(@"value=""disable:1"">[^<]*</button>\s+<button", rules);
    }

    [Fact]
    public void TheSizes_AreInKilobytesUnderAMegabyte()
    {
        var strings = _context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<LogsStrings>>();

        Assert.Equal(["0 Ko", "1 Ko", "1 024 Ko", "2 Mo"], new long[] { 0, 1, 1024 * 1024 - 1, 1024 * 1024 + 1 }.Select(size => LogsFormat.Size(size, strings).Replace(' ', ' ')));
    }
}
