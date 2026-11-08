// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// End to end, in a real ASP.NET Core host: the two registration lines, the recorder hearing the
/// hosting metrics without any middleware, and the page answering with a complete unstyled document.
/// </summary>
public sealed class PerformanceEndpointTests
{
    [Fact]
    public async Task TheTwoLines_MeasureTheSite_AndThePageShowsEachRoute()
    {
        await using var app = await StartAsync(app => app.MapGet("/orders/{id}", (int id) => id));
        using var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orders/7", ct)).StatusCode);
        await WaitForRouteAsync(app, "/orders/{id}", ct);
        var response = await client.GetAsync(PerformanceEndpointRouteBuilderExtensions.DefaultPattern, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("<code>/orders/{id}</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/orders/7", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACustomAddress_ServesThePage_AndThePageNeverMeasuresItself()
    {
        await using var app = await StartAsync(app => app.MapGet("/orders", () => "ok"), pattern: "/admin/performance");
        using var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PerformanceEndpointRouteBuilderExtensions.DefaultPattern, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/performance", ct)).StatusCode);
        await client.GetAsync("/orders", ct);
        await WaitForRouteAsync(app, "/orders", ct); // recorded after the page call, so the page had its chance

        Assert.DoesNotContain(Routes(app), route => route.Contains("performance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARealRequest_RaisesTheChangeSignal()
    {
        await using var app = await StartAsync(app => app.MapGet("/orders", () => "ok"));
        using var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var recorder = app.Services.GetRequiredService<RequestPerformanceRecorder>();
        var change = recorder.WaitForChangeAsync(ct);

        await client.GetAsync("/orders", ct);

        await change.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Contains("/orders", Routes(app));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankAddress_IsRefused(string pattern)
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapOmniPerformance(pattern));
    }

    [Fact]
    public async Task TwoHostsInOneProcess_NeverMixTheirFigures()
    {
        await using var first = await StartAsync(app => app.MapGet("/first", () => "1"));
        await using var second = await StartAsync(app => app.MapGet("/second", () => "2"));
        var ct = TestContext.Current.CancellationToken;

        using (var client = first.GetTestClient())
            await client.GetAsync("/first", ct);
        using (var client = second.GetTestClient())
            await client.GetAsync("/second", ct);
        await WaitForRouteAsync(first, "/first", ct);
        await WaitForRouteAsync(second, "/second", ct);

        Assert.DoesNotContain(Routes(first), route => route == "/second");
        Assert.DoesNotContain(Routes(second), route => route == "/first");
    }

    [Fact]
    public async Task AddOmniPerformance_TwiceAndBesideAddRazorComponents_RegistersOneRecorder()
    {
        await using var app = await StartAsync(_ => { }, services => services.AddRazorComponents().Services.AddOmniPerformance());

        Assert.Single(app.Services.GetServices<RequestPerformanceRecorder>());
    }

    [Fact]
    public async Task TheCollectorAlone_MeasuresTheSite_WithoutThePageServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOmniPerformanceCollector(options => options.Window = TimeSpan.FromHours(24));
        await using var app = builder.Build();
        app.MapGet("/api/orders", () => "ok");
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();

        await client.GetAsync("/api/orders", ct);
        await WaitForRouteAsync(app, "/api/orders", ct);

        Assert.Null(app.Services.GetService<Microsoft.Extensions.Localization.IStringLocalizerFactory>());
        Assert.Null(app.Services.GetService<PerformancePageServices>());
        Assert.Equal(TimeSpan.FromHours(24), app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PerformanceOptions>>().Value.Window);
    }

    [Fact]
    public void ThePage_OnAHostWithTheCollectorAlone_IsRefusedAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniPerformanceCollector();
        var app = builder.Build();

        var refusal = Assert.Throws<InvalidOperationException>(() => app.MapOmniPerformance());
        Assert.Contains("AddOmniPerformance()", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCollectorThenThePage_RegisterOneRecorder_AndServeThePage()
    {
        await using var app = await StartAsync(_ => { }, services => services.AddOmniPerformanceCollector().AddOmniPerformance());
        using var client = app.GetTestClient();

        Assert.Single(app.Services.GetServices<RequestPerformanceRecorder>());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PerformanceEndpointRouteBuilderExtensions.DefaultPattern, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task StopAsync_StopsCollectingTheHostsMetrics_AndCanBeRepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        using var recorder = new RequestPerformanceRecorder(
            Microsoft.Extensions.Options.Options.Create(new PerformanceOptions()), TimeProvider.System, factory);
        using var meter = factory.Create(new MeterOptions(RequestPerformanceRecorder.MeterName));
        var duration = meter.CreateHistogram<double>(RequestPerformanceRecorder.InstrumentName);
        KeyValuePair<string, object?>[] tags =
        [
            new("http.route", "/orders/{id}"),
            new("http.request.method", "GET"),
            new("http.response.status_code", 200)
        ];

        await recorder.StopAsync(ct); // stopping before startup is harmless
        await recorder.StartAsync(ct);
        duration.Record(0.010, tags);
        Assert.Equal(1, recorder.Summarize().SampleCount);

        await recorder.StopAsync(ct);
        duration.Record(0.020, tags);

        var summary = recorder.Summarize();
        Assert.Equal(1, summary.SampleCount);
        Assert.Equal(10d, Assert.Single(summary.Slowest).DurationMs);
        await recorder.StopAsync(ct);
    }

    [Fact]
    public async Task Settings_ComeFromTheHostConfiguration_ThenTheCodeOverrides()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["OmniPerformance:Window"] = "3.00:00:00";
        builder.Configuration["OmniPerformance:SlowestCount"] = "50";
        builder.Services.AddOmniPerformance(options => options.SlowestCount = 100);
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PerformanceOptions>>().Value;

        Assert.Equal((TimeSpan.FromDays(3), 100, 2L * 1024 * 1024), (options.Window, options.SlowestCount, options.MemoryLimitBytes));
    }

    private static async Task<WebApplication> StartAsync(
        Action<WebApplication> map, Action<IServiceCollection>? register = null, string pattern = PerformanceEndpointRouteBuilderExtensions.DefaultPattern)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOmniPerformance();
        register?.Invoke(builder.Services);
        var app = builder.Build();
        map(app);
        app.MapOmniPerformance(pattern);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static IEnumerable<string> Routes(WebApplication app) =>
        app.Services.GetRequiredService<RequestPerformanceRecorder>().Summarize().Routes.Select(route => route.Route);

    /// <summary>ASP.NET Core records the duration once the response has completed, a moment after the client
    /// has read it: wait for the sample instead of racing it.</summary>
    private static async Task WaitForRouteAsync(WebApplication app, string route, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!Routes(app).Contains(route))
            await Task.Delay(20, timeout.Token);
    }
}
