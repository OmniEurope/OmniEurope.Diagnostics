// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Components;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The figures as the page writes them from a recorder fed by hand: semantic tables without a class or a style,
/// the texts from the package's resources in French (neutral) and English.
/// </summary>
public sealed class PerformancePageTests : IDisposable
{
    private static readonly DateTimeOffset ProcessStart = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly BunitContext _context = new();
    private readonly ManualClock _clock = new(ProcessStart);
    private TestRecorder? _recorder;

    public PerformancePageTests() => _context.Services.AddLocalization();

    public void Dispose()
    {
        _context.Dispose();
        _recorder?.Dispose();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Fact]
    public void WithSamples_WritesTheWindow_TheSlowestCalls_AndOneLinePerRoute()
    {
        UseCulture("fr-FR");
        var recorder = UseRecorder(new PerformanceOptions { SlowestCount = 1 });
        _clock.Advance(new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero) - ProcessStart);
        recorder.Record("orders/{id}", "GET", 200, 12);
        recorder.Record("orders/{id}", "GET", 200, 980);
        _clock.Advance(TimeSpan.FromMinutes(5));
        recorder.Record("orders/{id}", "GET", 200, 1234);

        var page = _context.Render<PerformancePage>();

        Assert.Contains("Depuis le 2026-09-29 08:35:00 (UTC), les plus lents d'abord.", page.Markup);
        Assert.Contains("Mesures : 3, depuis le 2026-10-06 08:30:00 (UTC).", page.Markup);
        Assert.DoesNotContain("Plafond atteint", page.Markup);
        var tables = page.FindAll("table");
        Assert.Equal(2, tables.Count);
        Assert.Equal(["Appels les plus lents", "Par route"], page.FindAll("h2").Select(h => h.TextContent));
        var slowest = tables[0].QuerySelectorAll("tbody td").Select(td => td.TextContent).ToArray();
        Assert.Equal(["1,23 s", "GET", "/orders/{id}", "200", "2026-10-06 08:35:00"], slowest);
        var route = tables[1].QuerySelectorAll("tbody td").Select(td => td.TextContent).ToArray();
        Assert.Equal(["GET", "/orders/{id}", "3", "980 ms", "1,23 s", "1,23 s", "1,23 s"], route);
        Assert.Empty(page.FindAll("[class], [style], style, link, script"));
    }

    [Fact]
    public void WithoutSamples_SaysSo_AndDrawsNoTable()
    {
        UseCulture("fr-FR");
        UseRecorder(new PerformanceOptions());

        var page = _context.Render<PerformancePage>();

        Assert.Contains("Aucune mesure pour l'instant.", page.Markup);
        Assert.Empty(page.FindAll("table"));
    }

    [Fact]
    public void PastTheCap_SaysThePercentilesLostTheOldest_InEnglish()
    {
        UseCulture("en-GB");
        // Room for a single request in the ring: the second one overwrites the first inside the window.
        var limit = RequestPerformanceRecorder.FixedOverheadBytes + SlowestRequests.ReservedBytes(20, TimeSpan.FromDays(7))
            + Unsafe.SizeOf<RequestTimingSample>();
        var recorder = UseRecorder(new PerformanceOptions { MemoryLimitBytes = limit });
        recorder.Record("orders", "GET", 200, 10);
        recorder.Record("orders", "GET", 200, 20);

        var page = _context.Render<PerformancePage>();

        Assert.Contains("Cap reached: the oldest measurements no longer count in these percentiles (the slowest calls list is not affected).", page.Markup);
        Assert.Equal(["Slowest calls", "By route"], page.FindAll("h2").Select(h => h.TextContent));
    }

    [Theory]
    [InlineData(19, true, true)]
    [InlineData(20, false, true)]
    [InlineData(99, false, true)]
    [InlineData(100, false, false)]
    public void APercentileThatIsTheMaximumItself_IsWrittenInSmallPrint_AndExplained(int calls, bool p95Small, bool p99Small)
    {
        UseCulture("fr-FR");
        var recorder = UseRecorder(new PerformanceOptions());
        for (var i = 1; i <= calls; i++) recorder.Record("orders", "GET", 200, i);

        var page = _context.Render<PerformancePage>();

        var cells = page.FindAll("table")[1].QuerySelectorAll("tbody td").ToArray();
        Assert.Equal(p95Small, cells[4].QuerySelector("small") is not null);
        Assert.Equal(p99Small, cells[5].QuerySelector("small") is not null);
        Assert.Null(cells[3].QuerySelector("small"));
        Assert.Null(cells[6].QuerySelector("small"));
        Assert.Equal(p99Small, page.Markup.Contains("En petit : un centile égal au maximum", StringComparison.Ordinal));
        if (p95Small) Assert.Equal(cells[6].TextContent, cells[4].TextContent);
        else Assert.NotEqual(cells[6].TextContent, cells[4].TextContent);
        if (p99Small) Assert.Equal(cells[6].TextContent, cells[5].TextContent);
        else Assert.NotEqual(cells[6].TextContent, cells[5].TextContent);
        Assert.Empty(page.FindAll("[class], [style], style, link, script"));
    }

    [Fact]
    public void TheSmallPrintNote_IsTranslated()
    {
        UseCulture("en-GB");
        var recorder = UseRecorder(new PerformanceOptions());
        recorder.Record("orders", "GET", 200, 10);

        var page = _context.Render<PerformancePage>();

        Assert.Contains("In small print: a percentile equal to the maximum", page.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-GB", "en", "Site performance")]
    [InlineData("fr-BE", "fr", "Performances du site")]
    [InlineData("de-DE", "fr", "Performances du site")]
    [InlineData("es-ES", "fr", "Performances du site")]
    [InlineData("", "fr", "Performances du site")] // invariant culture: its two-letter name "iv" is no language
    public void ThePageLanguage_MatchesTheRenderedTexts_IncludingTheNeutralFallback(string culture, string expected, string title)
    {
        UseCulture(culture);
        UseRecorder(new PerformanceOptions());

        var page = _context.Render<PerformancePage>();

        Assert.Contains($"<html lang=\"{expected}\">", page.Markup, StringComparison.Ordinal);
        Assert.Equal(title, page.Find("h1").TextContent);
    }

    private RequestPerformanceRecorder UseRecorder(PerformanceOptions options)
    {
        _recorder = new TestRecorder(_clock, options);
        _context.Services.AddSingleton(_recorder.Recorder);
        // The page is rendered statically by its endpoint (set once the services are complete).
        _context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        return _recorder.Recorder;
    }

    private static void UseCulture(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
    }
}
