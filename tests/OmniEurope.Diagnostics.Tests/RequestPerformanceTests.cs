// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The arithmetic of the report on its own, then the recorder's filters, window, cap and slowest list
/// through its internal entry point, with a clock the test moves. The listener itself is proven end to
/// end by <see cref="PerformanceEndpointTests"/>.
/// </summary>
public sealed class RequestPerformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Percentiles_AreNearestRank_SoEveryFigureIsARealRequest()
    {
        double[] ascending = [10, 20, 30, 40, 50, 60, 70, 80, 90, 1000];

        Assert.Equal(50, ascending[RequestPerformanceReport.NearestRank(ascending.Length, 0.50)]);
        Assert.Equal(1000, ascending[RequestPerformanceReport.NearestRank(ascending.Length, 0.95)]);
        Assert.Equal(0, RequestPerformanceReport.NearestRank(1, 0.99));
        Assert.Equal(0, RequestPerformanceReport.NearestRank(0, 0.99));
    }

    [Fact]
    public void Report_RanksRoutesByTheir95thPercentile()
    {
        var at = Now.UtcDateTime;
        var samples = Enumerable.Range(1, 30)
            .Select(i => new RequestTimingSample(at.AddMinutes(-i), "GET", "/api/projects", 200, i))
            .Append(new RequestTimingSample(at, "GET", "/api/pipelines/{id}", 200, 900))
            .ToArray();

        var report = RequestPerformanceReport.Build(samples, truncated: false, [], at.AddDays(-7));

        Assert.Equal(31, report.SampleCount);
        Assert.False(report.Truncated);
        Assert.Equal("/api/pipelines/{id}", report.Routes[0].Route);
        var projects = report.Routes.Single(route => route.Route == "/api/projects");
        Assert.Equal((30, 15d, 29d, 30d, 30d), (projects.Count, projects.P50Ms, projects.P95Ms, projects.P99Ms, projects.MaxMs));
        Assert.Equal(at.AddMinutes(-30), report.Since);
    }

    [Fact]
    public void Report_WithoutSamples_HasNoPercentileStart()
    {
        var report = RequestPerformanceReport.Build([], truncated: false, [], Now.UtcDateTime);

        Assert.Null(report.Since);
        Assert.Empty(report.Routes);
        Assert.Empty(report.Slowest);
    }

    [Theory]
    [InlineData("/orders/{id}", true)]
    [InlineData("/", true)]
    [InlineData("/api/v1.0/orders", true)]
    [InlineData("/files/{name}.pdf", true)]
    [InlineData("/healthcare/{id}", true)]
    [InlineData("/health-report", true)]
    [InlineData("/health", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/Health/Ready", false)]
    [InlineData("/_content/lib/site.css", false)]
    [InlineData("/robots.txt", false)]
    [InlineData("/_framework/blazor.web.js", false)]
    [InlineData("/_blazor", false)]
    [InlineData("/css/app.css", false)]
    [InlineData("/favicon.ico", false)]
    public void DefaultFilter_KeepsPages_AndDropsProbesFrameworkAndStaticFiles(string route, bool measured) =>
        Assert.Equal(measured, RequestPerformanceRecorder.IsMeasuredByDefault(route));

    [Fact]
    public void Recorder_NormalizesTheTemplate_AndIgnoresUnmatchedRequests()
    {
        using var test = new TestRecorder(new ManualClock(Now));

        test.Recorder.Record("Orders/{id}", "GET", 200, 12);
        test.Recorder.Record("/orders/{id}", "GET", 200, 30);
        test.Recorder.Record("health/ready", "GET", 200, 1);
        test.Recorder.Record(null, "GET", 404, 1);

        var summary = test.Recorder.Summarize();
        var route = Assert.Single(summary.Routes);
        Assert.Equal(("/orders/{id}", 2), (route.Route, route.Count));
        Assert.All(summary.Slowest, sample => Assert.Equal(Now.UtcDateTime, sample.At));
    }

    [Fact]
    public void Recorder_AppliesTheHostFilter_InsteadOfTheDefault()
    {
        var options = new PerformanceOptions { RouteFilter = route => route.StartsWith("/api/", StringComparison.Ordinal) };
        using var test = new TestRecorder(new ManualClock(Now), options);

        test.Recorder.Record("orders", "GET", 200, 5);
        test.Recorder.Record("api/orders", "GET", 200, 5);

        Assert.Equal("/api/orders", Assert.Single(test.Recorder.Summarize().Routes).Route);
    }

    [Fact]
    public void Recorder_KeepsSevenDaysByDefault_ThenForgets()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock);

        test.Recorder.Record("old", "GET", 200, 500);
        clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(Now.UtcDateTime, test.Recorder.Summarize().WindowStart); // the process started less than 7 days ago

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        test.Recorder.Record("recent", "GET", 200, 7);
        var summary = test.Recorder.Summarize();

        Assert.Equal("/recent", Assert.Single(summary.Routes).Route);
        Assert.Equal("/recent", Assert.Single(summary.Slowest).Route);
        Assert.Equal(clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(7), summary.WindowStart);
    }

    [Fact]
    public void Recorder_HonoursAConfiguredWindow()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock, new PerformanceOptions { Window = TimeSpan.FromHours(1) });

        test.Recorder.Record("old", "GET", 200, 500);
        clock.Advance(TimeSpan.FromMinutes(61));
        test.Recorder.Record("recent", "GET", 200, 7);

        Assert.Equal("/recent", Assert.Single(test.Recorder.Summarize().Slowest).Route);
    }

    [Fact]
    public void SlowestList_KeepsTheConfiguredCount_SlowestFirst_AcrossHours()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock, new PerformanceOptions { SlowestCount = 3 });

        foreach (var duration in new double[] { 40, 900, 10 })
            test.Recorder.Record("a", "GET", 200, duration);
        clock.Advance(TimeSpan.FromHours(5));
        foreach (var duration in new double[] { 300, 20, 600 })
            test.Recorder.Record("b", "GET", 200, duration);

        Assert.Equal([900d, 600, 300], test.Recorder.Summarize().Slowest.Select(sample => sample.DurationMs));
    }


    [Fact]
    public void PastTheMemoryLimit_TheRingRolls_TheWorstCallsSurvive_AndThePercentilesSaySoUntilTheDroppedOnesAgeOut()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock, new PerformanceOptions { MemoryLimitBytes = LimitForRing(3) });

        test.Recorder.Record("worst", "GET", 500, 5_000);
        clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 3; i++)
            test.Recorder.Record("orders", "GET", 200, 2);

        var summary = test.Recorder.Summarize();
        Assert.True(summary.Truncated);
        Assert.Equal(3, summary.SampleCount);
        Assert.DoesNotContain(summary.Routes, route => route.Route == "/worst");
        Assert.Equal("/worst", summary.Slowest[0].Route);

        clock.Advance(TimeSpan.FromDays(6) + TimeSpan.FromMinutes(1)); // the overwritten request leaves the window
        Assert.False(test.Recorder.Summarize().Truncated);
    }

    [Fact]
    public void TheRing_KeepsTheNewestRequests_OldestFirst()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock, new PerformanceOptions { MemoryLimitBytes = LimitForRing(2) });

        foreach (var route in new[] { "a", "b", "c" })
        {
            test.Recorder.Record(route, "GET", 200, 1);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var summary = test.Recorder.Summarize();
        Assert.Equal(["/b", "/c"], summary.Routes.Select(route => route.Route).Order());
        Assert.Equal(Now.UtcDateTime.AddMinutes(1), summary.Since);
    }

    [Theory]
    [InlineData(5_000_000)]
    [InlineData(10_000_000)]
    public void SlowestBudget_SaturatesInsteadOfWrappingForAnUnrepresentableReservation(int windowDays)
    {
        Assert.Equal(long.MaxValue, SlowestRequests.ReservedBytes(int.MaxValue, TimeSpan.FromDays(windowDays)));
    }

    [Theory]
    [InlineData(5_000_000, 2 * 1024 * 1024)]
    [InlineData(5_000_000, long.MaxValue)]
    [InlineData(10_000_000, long.MaxValue)]
    public void Recorder_RefusesAnUnrepresentableReservation_BeforeAllocating(int windowDays, long memoryLimitBytes)
    {
        var options = new PerformanceOptions
        {
            Window = TimeSpan.FromDays(windowDays),
            SlowestCount = int.MaxValue,
            MemoryLimitBytes = memoryLimitBytes
        };

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => new TestRecorder(new ManualClock(Now), options));

        Assert.Equal("options.MemoryLimitBytes", refusal.ParamName);
    }

    [Theory]
    [InlineData(0, 20, 2 * 1024 * 1024)]
    [InlineData(1, 0, 2 * 1024 * 1024)]
    [InlineData(168, 20, 100_000)] // the slowest list of 7 days alone needs more than 100 kB
    [InlineData(1, 20, long.MinValue)] // must not wrap around into a huge budget
    [InlineData(1, 20, -1)]
    public void Recorder_RefusesASettingThatLeavesNothingToMeasure(int windowHours, int slowestCount, long memoryLimitBytes)
    {
        var options = new PerformanceOptions
        {
            Window = TimeSpan.FromHours(windowHours),
            SlowestCount = slowestCount,
            MemoryLimitBytes = memoryLimitBytes
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TestRecorder(new ManualClock(Now), options));
    }

    [Fact]
    public void Recorder_RefusesAWindowThatStartsBeforeTheCalendar()
    {
        var start = new DateTimeOffset(DateTime.MinValue.AddDays(7), TimeSpan.Zero);
        var longest = TimeSpan.FromDays(7);

        using var accepted = new TestRecorder(new ManualClock(start), new PerformanceOptions { Window = longest });
        accepted.Recorder.Record("/orders", "GET", 200, 5);
        Assert.Equal(1, accepted.Recorder.Summarize().SampleCount);

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => new TestRecorder(
            new ManualClock(start), new PerformanceOptions { Window = longest + TimeSpan.FromTicks(1) }));

        Assert.Equal("options.Window", refusal.ParamName);
    }

    /// <summary>The memory limit that leaves room for exactly <paramref name="samples"/> rolling samples beside
    /// the default slowest list of the default window.</summary>
    private static long LimitForRing(int samples) =>
        RequestPerformanceRecorder.FixedOverheadBytes + SlowestRequests.ReservedBytes(20, TimeSpan.FromDays(7)) + samples * System.Runtime.CompilerServices.Unsafe.SizeOf<RequestTimingSample>();
}
