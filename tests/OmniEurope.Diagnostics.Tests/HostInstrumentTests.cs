// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The host's own histograms of <see cref="PerformanceOptions.Instruments"/>, heard on real meters created with
/// <c>new Meter</c> (no scope), and the recorder started without a host. Every test names its own meter: these
/// meters are heard whatever their scope, so a shared name would let parallel tests hear each other.
/// </summary>
public sealed class HostInstrumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASecondsHistogram_IsRecordedInMilliseconds_UnderItsNameTag()
    {
        using var meter = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.import.duration");
        test.Recorder.Start();
        var histogram = meter.CreateHistogram<double>("app.import.duration", unit: "s");

        histogram.Record(1.5, new KeyValuePair<string, object?>("operation", "Import"));

        var sample = Assert.Single(test.Recorder.Summarize().Slowest);
        Assert.Equal(("Import", 1500d, true, true), (sample.Route, sample.DurationMs, sample.IsOperation, sample.Succeeded));
    }

    [Fact]
    public void AMillisecondsHistogramOfLongs_PublishedBeforeTheStart_IsHeard_WithItsOwnNameTag()
    {
        using var meter = new Meter(UniqueMeterName());
        var histogram = meter.CreateHistogram<long>("app.screen.duration", unit: "ms");
        using var test = Recorder(meter.Name, "app.screen.duration", nameTag: "screen");
        test.Recorder.Start();

        histogram.Record(320, new KeyValuePair<string, object?>("screen", "Settings"));
        histogram.Record(5, new KeyValuePair<string, object?>("operation", "Ignored"));

        var sample = Assert.Single(test.Recorder.Summarize().Slowest);
        Assert.Equal(("Settings", 320d), (sample.Route, sample.DurationMs));
    }

    [Fact]
    public void AMeasurementWithoutAUsableName_OrDuration_IsIgnored()
    {
        using var meter = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.duration");
        test.Recorder.Start();
        var histogram = meter.CreateHistogram<double>("app.duration", unit: "ms");

        histogram.Record(10);
        histogram.Record(10, new KeyValuePair<string, object?>("operation", " "));
        histogram.Record(10, new KeyValuePair<string, object?>("operation", 42));
        histogram.Record(-1, new KeyValuePair<string, object?>("operation", "Import"));
        histogram.Record(double.NaN, new KeyValuePair<string, object?>("operation", "Import"));

        Assert.Equal(0, test.Recorder.Summarize().SampleCount);
    }

    [Theory]
    [InlineData("error.type", "timeout", false)]
    [InlineData("error.type", null, true)]
    [InlineData("outcome", "failure", false)]
    [InlineData("outcome", "Error", false)]
    [InlineData("outcome", "success", true)]
    [InlineData("outcome", 1, true)]
    [InlineData("result", "failure", true)]
    public void TheFailureTags_MarkTheOperationFailed(string key, object? value, bool succeeded)
    {
        using var meter = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.duration");
        test.Recorder.Start();
        var histogram = meter.CreateHistogram<double>("app.duration", unit: "ms");

        histogram.Record(10, new KeyValuePair<string, object?>("operation", "Import"), new KeyValuePair<string, object?>(key, value));

        Assert.Equal(succeeded, Assert.Single(test.Recorder.Summarize().Slowest).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("min")]
    [InlineData("By")]
    public void AHistogramWithoutASecondsOrMillisecondsUnit_IsNotHeard(string? unit)
    {
        using var meter = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.duration");
        test.Recorder.Start();
        var histogram = meter.CreateHistogram<double>("app.duration", unit: unit);

        histogram.Record(10, new KeyValuePair<string, object?>("operation", "Import"));

        Assert.Equal(0, test.Recorder.Summarize().SampleCount);
    }

    [Fact]
    public void OnlyTheNamedHistograms_AreHeard()
    {
        using var meter = new Meter(UniqueMeterName());
        using var other = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.duration");
        test.Recorder.Start();

        meter.CreateCounter<double>("app.duration", unit: "ms").Add(1, new KeyValuePair<string, object?>("operation", "Counter"));
        meter.CreateHistogram<int>("app.duration", unit: "ms").Record(1, new KeyValuePair<string, object?>("operation", "Int"));
        meter.CreateHistogram<double>("app.other", unit: "ms").Record(1, new KeyValuePair<string, object?>("operation", "Other"));
        other.CreateHistogram<double>("app.duration", unit: "ms").Record(1, new KeyValuePair<string, object?>("operation", "OtherMeter"));
        meter.CreateHistogram<double>("app.duration", unit: "ms").Record(1, new KeyValuePair<string, object?>("operation", "Heard"));

        Assert.Equal("Heard", Assert.Single(test.Recorder.Summarize().Slowest).Route);
    }

    [Fact]
    public async Task NothingIsHeard_BeforeStart_OrAfterStop_ButRecordOperationAlwaysWorks()
    {
        using var meter = new Meter(UniqueMeterName());
        using var test = Recorder(meter.Name, "app.duration");
        var histogram = meter.CreateHistogram<double>("app.duration", unit: "ms");
        var tag = new KeyValuePair<string, object?>("operation", "Heard");

        histogram.Record(1, tag);
        test.Recorder.RecordOperation("Direct", TimeSpan.FromMilliseconds(1));
        test.Recorder.Start();
        test.Recorder.Start();
        histogram.Record(2, tag);
        await test.Recorder.StopAsync(TestContext.Current.CancellationToken);
        histogram.Record(3, tag);

        Assert.Equal([1d, 2d], test.Recorder.Summarize().Slowest.Select(sample => sample.DurationMs).Order());
    }

    [Fact]
    public async Task TheStartOfTheHost_StartsTheListener_AndADisposedRecorderCannotStart()
    {
        using var meter = new Meter(UniqueMeterName());
        var test = Recorder(meter.Name, "app.duration");
        var histogram = meter.CreateHistogram<double>("app.duration", unit: "ms");

        await test.Recorder.StartAsync(TestContext.Current.CancellationToken);
        histogram.Record(4, new KeyValuePair<string, object?>("operation", "Import"));
        test.Dispose();
        histogram.Record(5, new KeyValuePair<string, object?>("operation", "Import"));

        Assert.Single(test.Recorder.Summarize().Slowest);
        Assert.Throws<ObjectDisposedException>(test.Recorder.Start);
    }

    [Theory]
    [InlineData("", "app.duration", "operation")]
    [InlineData("App", " ", "operation")]
    [InlineData("App", "app.duration", "")]
    public void AnIncompleteInstrument_IsRefusedWhenTheRecorderIsCreated(string meterName, string instrumentName, string nameTag)
    {
        var options = new PerformanceOptions();
        options.Instruments.Add(new PerformanceInstrument { MeterName = meterName, InstrumentName = instrumentName, NameTag = nameTag });

        Assert.Throws<ArgumentException>(() => new TestRecorder(new ManualClock(Now), options));
    }

    [Fact]
    public void ANullInstrument_IsRefusedWhenTheRecorderIsCreated()
    {
        var options = new PerformanceOptions();
        options.Instruments.Add(null!);

        Assert.Throws<ArgumentException>(() => new TestRecorder(new ManualClock(Now), options));
    }

    [Fact]
    public void TheInstruments_AreReadFromConfiguration_ThroughTheCollector()
    {
        var meterName = UniqueMeterName();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OmniPerformance:Instruments:0:MeterName"] = meterName,
            ["OmniPerformance:Instruments:0:InstrumentName"] = "app.duration",
        }).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddOmniPerformanceCollector(options => options.Instruments.Add(new PerformanceInstrument
        {
            MeterName = meterName, InstrumentName = "app.save.duration", NameTag = "step",
        }));
        using var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredService<RequestPerformanceRecorder>();
        recorder.Start();
        using var meter = new Meter(meterName);

        meter.CreateHistogram<double>("app.duration", unit: "ms").Record(7, new KeyValuePair<string, object?>("operation", "Import"));
        meter.CreateHistogram<double>("app.save.duration", unit: "s").Record(0.5, new KeyValuePair<string, object?>("step", "Save"));

        Assert.Equal(["Import", "Save"], recorder.Summarize().Routes.Select(route => route.Route).Order());
        Assert.Equal(PerformanceInstrument.DefaultNameTag, provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PerformanceOptions>>().Value.Instruments[0].NameTag);
    }

    private static TestRecorder Recorder(string meterName, string instrumentName, string nameTag = PerformanceInstrument.DefaultNameTag)
    {
        var options = new PerformanceOptions();
        options.Instruments.Add(new PerformanceInstrument { MeterName = meterName, InstrumentName = instrumentName, NameTag = nameTag });
        return new TestRecorder(new ManualClock(Now), options);
    }

    private static string UniqueMeterName() => "OmniEurope.Diagnostics.Tests." + Guid.NewGuid().ToString("N");
}
