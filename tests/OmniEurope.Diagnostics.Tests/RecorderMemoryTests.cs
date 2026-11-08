// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The memory limit measured, not computed: every byte this thread allocates while it creates a recorder
/// and fills each hour of its 7-day window, ring rolling, is counted. What the recorder keeps is a part of
/// what it allocated, so the count bounds it from above, free of the other threads' noise that a
/// whole-heap measure picks up.
/// </summary>
public sealed class RecorderMemoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Routes = ["orders/{id}", "api/customers", "reports/{year}/{month}"];

    [Fact]
    public void AFullWeek_AllocatesNoMoreThanTheDefaultTwoMebibytes()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = provider.GetRequiredService<IMeterFactory>();
        var limit = new PerformanceOptions().MemoryLimitBytes;
        // A first run outside the count: the runtime's one-time allocations (generic instantiations,
        // comparers, lazily built statics) are not the recorder's.
        Fill(meters);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var recorder = Fill(meters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current.TestOutputHelper?.WriteLine($"Recorder allocated {allocated:N0} bytes for a limit of {limit:N0}.");
        Assert.True(recorder.Summarize().Truncated, "The ring must have rolled, or the count says nothing about a full recorder.");
        Assert.InRange(allocated, limit * 8 / 10, limit);
    }

    [Fact]
    public void ReadingAFullWeek_AllocatesNoMoreThanOneCopyOfTheRing()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = provider.GetRequiredService<IMeterFactory>();
        var limit = new PerformanceOptions().MemoryLimitBytes;
        var recorder = Fill(meters);
        recorder.Summarize(); // one-time runtime allocations (sort comparers, generic instantiations) left out

        var before = GC.GetAllocatedBytesForCurrentThread();
        var summary = recorder.Summarize();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current.TestOutputHelper?.WriteLine($"Summarize allocated {allocated:N0} bytes for a limit of {limit:N0}.");
        Assert.True(summary.SampleCount > 40_000, "The ring must be full, or the count says nothing about a heavy read.");
        Assert.Equal(3, summary.Routes.Count);
        Assert.InRange(allocated, summary.SampleCount * 40L, limit);
    }
    /// <summary>A recorder with 400 requests in each hour of the week: every bucket of the slowest list full,
    /// the ring rolled many times over.</summary>
    private static RequestPerformanceRecorder Fill(IMeterFactory meters)
    {
        var clock = new ManualClock(Start);
        var recorder = new RequestPerformanceRecorder(Options.Create(new PerformanceOptions()), clock, meters);
        for (var hour = 0; hour < 7 * 24; hour++)
        {
            for (var i = 0; i < 400; i++)
                recorder.Record(Routes[i % Routes.Length], "GET", 200, (hour * 400 + i) % 997);
            clock.Advance(TimeSpan.FromHours(1));
        }
        return recorder;
    }
}
