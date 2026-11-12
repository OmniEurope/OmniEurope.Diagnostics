// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The operations a host records itself, directly or through a timing scope: filed under their name as given,
/// never filtered as routes, sharing the window, the slowest list and the change signal with the requests.
/// </summary>
public sealed class OperationRecordingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARecordedOperation_IsASampleWithoutMethod_UnderItsTrimmedName_CaseKept()
    {
        using var test = new TestRecorder(new ManualClock(Now));

        test.Recorder.RecordOperation("  Import File ", TimeSpan.FromMilliseconds(42));
        test.Recorder.RecordOperation("Import File", TimeSpan.FromMilliseconds(8), succeeded: false);

        var summary = test.Recorder.Summarize();
        Assert.Equal(2, summary.SampleCount);
        var slowest = summary.Slowest[0];
        Assert.Equal(new RequestTimingSample(Now.UtcDateTime, "", "Import File", RequestTimingSample.OperationSucceeded, 42), slowest);
        Assert.True(slowest.IsOperation);
        Assert.True(slowest.Succeeded);
        var failed = summary.Slowest[1];
        Assert.Equal(RequestTimingSample.OperationFailed, failed.StatusCode);
        Assert.False(failed.Succeeded);
        var line = Assert.Single(summary.Routes);
        Assert.True(line.IsOperation);
        Assert.Equal(("Import File", 2, 42d), (line.Route, line.Count, line.MaxMs));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(404, true)]
    [InlineData(500, false)]
    public void ARequestSample_IsNoOperation_AndSucceedsBelowAServerError(int status, bool succeeded)
    {
        var sample = new RequestTimingSample(Now.UtcDateTime, "GET", "/orders", status, 1);

        Assert.False(sample.IsOperation);
        Assert.Equal(succeeded, sample.Succeeded);
        Assert.False(new RouteTimingSummary("GET", "/orders", 1, 1, 1, 1, 1).IsOperation);
        Assert.True(default(RequestTimingSample).IsOperation);
    }

    [Fact]
    public void Operations_AreNeverFilteredAsRoutes()
    {
        using var test = new TestRecorder(new ManualClock(Now), new PerformanceOptions { RouteFilter = _ => false });

        test.Recorder.RecordOperation("/health", TimeSpan.FromMilliseconds(3));
        test.Recorder.RecordOperation("site.css", TimeSpan.FromMilliseconds(4));
        test.Recorder.Record("/orders", "GET", 200, 5);

        Assert.Equal(["/health", "site.css"], test.Recorder.Summarize().Routes.Select(route => route.Route).Order());
    }

    [Fact]
    public void AnOperation_AndARequestOfTheSameName_StayOnTwoLines()
    {
        using var test = new TestRecorder(new ManualClock(Now));

        test.Recorder.RecordOperation("/orders", TimeSpan.FromMilliseconds(3));
        test.Recorder.Record("/orders", "GET", 200, 5);

        Assert.Equal(["", "GET"], test.Recorder.Summarize().Routes.Select(route => route.Method).Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnOperationWithoutName_IsRefused(string? name)
    {
        using var test = new TestRecorder(new ManualClock(Now));

        Assert.ThrowsAny<ArgumentException>(() => test.Recorder.RecordOperation(name!, TimeSpan.FromMilliseconds(1)));
        Assert.ThrowsAny<ArgumentException>(() => test.Recorder.StartOperation(name!));
        Assert.Equal(0, test.Recorder.Summarize().SampleCount);
    }

    [Fact]
    public void ANegativeDuration_IsRefused()
    {
        using var test = new TestRecorder(new ManualClock(Now));

        Assert.Throws<ArgumentOutOfRangeException>(() => test.Recorder.RecordOperation("Import", TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void ATimingScope_RecordsTheElapsedTimeOnce_OnTheRecordersClock()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock);

        var timing = test.Recorder.StartOperation("Load screen");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        timing.Dispose();
        clock.Advance(TimeSpan.FromSeconds(1));
        timing.Dispose();

        Assert.Equal("Load screen", timing.Name);
        var sample = Assert.Single(test.Recorder.Summarize().Slowest);
        Assert.Equal(("Load screen", 250d, true, Now.UtcDateTime.AddMilliseconds(250)), (sample.Route, sample.DurationMs, sample.Succeeded, sample.At));
    }

    [Fact]
    public void AFailedTimingScope_IsRecordedFailed()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock);

        using (var timing = test.Recorder.StartOperation("Save"))
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            timing.Fail();
        }

        var sample = Assert.Single(test.Recorder.Summarize().Slowest);
        Assert.Equal((2000d, false), (sample.DurationMs, sample.Succeeded));
    }

    [Fact]
    public void Operations_ShareTheSlowestList_AndTheWindow_WithTheRequests()
    {
        var clock = new ManualClock(Now);
        using var test = new TestRecorder(clock, new PerformanceOptions { SlowestCount = 1, Window = TimeSpan.FromHours(1) });

        test.Recorder.Record("/orders", "GET", 200, 10);
        test.Recorder.RecordOperation("Import", TimeSpan.FromMilliseconds(50));

        var summary = test.Recorder.Summarize();
        Assert.Equal("Import", Assert.Single(summary.Slowest).Route);
        Assert.Equal(2, summary.SampleCount);

        clock.Advance(TimeSpan.FromHours(2));

        var later = test.Recorder.Summarize();
        Assert.Empty(later.Slowest);
        Assert.Equal(0, later.SampleCount);
    }

    [Fact]
    public async Task AnOperation_RaisesTheChangeSignal()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        var wait = test.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        test.Recorder.RecordOperation("Import", TimeSpan.FromMilliseconds(5));

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
