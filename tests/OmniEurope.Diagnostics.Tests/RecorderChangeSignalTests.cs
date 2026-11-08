// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The change signal a live page waits on: raised by a recorded request, coalesced, silent for a route the
/// filter leaves out. Fed through the recorder's internal entry point; the listener raising it from a real
/// request is proven by <see cref="PerformanceEndpointTests"/>.
/// </summary>
public sealed class RecorderChangeSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARecordedRequest_CompletesTheWait()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        var wait = test.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        test.Recorder.Record("/orders", "GET", 200, 5);

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ManyRequests_BetweenTwoWaits_RaiseOneSignal()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        for (var i = 0; i < 100; i++)
            test.Recorder.Record("/orders", "GET", 200, i);

        await test.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(test.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    [Fact]
    public void ARouteTheFilterLeavesOut_RaisesNoSignal()
    {
        using var test = new TestRecorder(new ManualClock(Now), new PerformanceOptions { RouteFilter = route => route.StartsWith("/api/", StringComparison.Ordinal) });

        test.Recorder.Record("/health", "GET", 200, 1);
        test.Recorder.Record("/orders", "GET", 200, 1);
        test.Recorder.Record(null, "GET", 404, 1);

        Assert.False(test.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    [Fact]
    public async Task ConcurrentRequests_NeverThrowOnTheRequestPath_AndWakeTheWaiter()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wakes = 0;
        var waiter = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await test.Recorder.WaitForChangeAsync(stop.Token);
                    Interlocked.Increment(ref wakes);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, TestContext.Current.CancellationToken);

        // Many requests raising the signal at once, while the waiter keeps taking it: a request that races
        // another must never see an exception, since the signal is raised from the listener callback.
        Parallel.For(0, 20_000, i => test.Recorder.Record("/orders", "GET", 200, i));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref wakes) == 0)
            await Task.Delay(10, timeout.Token);
        await stop.CancelAsync();
        await waiter;

        Assert.True(wakes >= 1);
        Assert.Equal(20_000, test.Recorder.Summarize().SampleCount);
    }

    [Fact]
    public async Task TwoWaiters_ShareTheSignals_EachSignalWakingOne()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        var ct = TestContext.Current.CancellationToken;
        Task[] waits = [test.Recorder.WaitForChangeAsync(ct), test.Recorder.WaitForChangeAsync(ct)];

        test.Recorder.Record("/orders", "GET", 200, 5);
        await Task.WhenAny(waits).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(1, waits.Count(wait => wait.IsCompleted));

        test.Recorder.Record("/orders", "GET", 200, 6);
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task ACancelledWait_EndsWithoutARequest()
    {
        using var test = new TestRecorder(new ManualClock(Now));
        using var cancel = new CancellationTokenSource();
        var wait = test.Recorder.WaitForChangeAsync(cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
