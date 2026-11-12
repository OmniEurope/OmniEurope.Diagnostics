// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Components;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The embeddable view: a fragment without a document, an operation shown with its outcome, and a refresh button
/// that reads the figures again where it can be clicked.
/// </summary>
public sealed class OmniPerformanceViewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly BunitContext _context = new();
    private readonly TestRecorder _recorder = new(new ManualClock(Now));

    public OmniPerformanceViewTests()
    {
        _context.Services.AddLocalization();
        _context.Services.AddSingleton(_recorder.Recorder);
    }

    public void Dispose()
    {
        _context.Dispose();
        _recorder.Dispose();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Fact]
    public void AnOperation_ShowsItsOutcome_InsteadOfAMethodAndAStatus()
    {
        UseCulture("fr-FR");
        _context.SetRendererInfo(new RendererInfo("WebView", isInteractive: true));
        _recorder.Recorder.RecordOperation("Import", TimeSpan.FromMilliseconds(900), succeeded: false);
        _recorder.Recorder.RecordOperation("Import", TimeSpan.FromMilliseconds(30));
        _recorder.Recorder.Record("/orders", "GET", 200, 12);

        var view = _context.Render<OmniPerformanceView>();

        var slowest = view.FindAll("table")[0].QuerySelectorAll("tbody tr")
            .Select(row => row.QuerySelectorAll("td").Take(4).Select(td => td.TextContent).ToArray()).ToArray();
        Assert.Equal(["900 ms", "Opération", "Import", "Échouée"], slowest[0]);
        Assert.Equal(["30 ms", "Opération", "Import", "Réussie"], slowest[1]);
        Assert.Equal(["12 ms", "GET", "/orders", "200"], slowest[2]);
        var routes = view.FindAll("table")[1].QuerySelectorAll("tbody tr")
            .Select(row => (row.Children[0].TextContent, row.Children[1].TextContent, row.Children[2].TextContent)).ToArray();
        Assert.Equal([("Opération", "Import", "2"), ("GET", "/orders", "1")], routes);
        Assert.Empty(view.FindAll("html, head, body, main, h1, [class], [style], style, link, script"));
    }

    [Fact]
    public void TheRefreshButton_ReadsTheFiguresAgain_InEnglish()
    {
        UseCulture("en-GB");
        _context.SetRendererInfo(new RendererInfo("WebView", isInteractive: true));
        var view = _context.Render<OmniPerformanceView>();
        Assert.Contains("No request measured yet.", view.Markup, StringComparison.Ordinal);

        _recorder.Recorder.RecordOperation("Load screen", TimeSpan.FromMilliseconds(40));
        Assert.Contains("No request measured yet.", view.Markup, StringComparison.Ordinal);
        var button = view.Find("button");
        Assert.Equal(("button", "Refresh"), (button.GetAttribute("type"), button.TextContent));
        button.Click();

        Assert.DoesNotContain("No request measured yet.", view.Markup, StringComparison.Ordinal);
        Assert.Equal(["40 ms", "Operation", "Load screen", "Succeeded"],
            view.FindAll("table")[0].QuerySelectorAll("tbody td").Take(4).Select(td => td.TextContent));
    }

    [Fact]
    public void StaticRendering_DrawsNoButton()
    {
        UseCulture("fr-FR");
        _context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        var view = _context.Render<OmniPerformanceView>();

        Assert.Empty(view.FindAll("button"));
        Assert.Contains("Mesures conservées en mémoire", view.Markup, StringComparison.Ordinal);
    }

    private static void UseCulture(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
    }
}
