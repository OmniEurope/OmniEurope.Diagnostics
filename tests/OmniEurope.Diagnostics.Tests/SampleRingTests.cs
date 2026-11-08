// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The ring on its own, fed out of order as two simultaneous requests can feed it: they are dated before
/// they take the ring's lock.
/// </summary>
public sealed class SampleRingTests
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnExpiredRequestBehindANewerOne_IsLeftOutOfTheWindow()
    {
        var ring = new SampleRing(3);
        ring.Add(Sample(Noon));
        ring.Add(Sample(Noon.AddHours(-1)));

        var (samples, truncated) = ring.Since(Noon.AddMinutes(-30));

        Assert.Equal([Noon], samples.Select(sample => sample.At));
        Assert.False(truncated);
    }

    [Fact]
    public void AnOlderOverwrite_DoesNotHideANewerOneStillInsideTheWindow()
    {
        var ring = new SampleRing(2);
        ring.Add(Sample(Noon));
        ring.Add(Sample(Noon.AddHours(-1)));
        ring.Add(Sample(Noon.AddHours(1))); // overwrites noon, still inside the window below
        ring.Add(Sample(Noon.AddHours(2))); // overwrites 11:00, already outside it

        var (samples, truncated) = ring.Since(Noon.AddMinutes(-30));

        Assert.Equal([Noon.AddHours(1), Noon.AddHours(2)], samples.Select(sample => sample.At));
        Assert.True(truncated);
    }

    private static RequestTimingSample Sample(DateTime at) => new(at, "GET", "/orders", 200, 1);
}
