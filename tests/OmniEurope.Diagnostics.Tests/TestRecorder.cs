// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>A recorder fed by hand, on its own meter factory.</summary>
internal sealed class TestRecorder : IDisposable
{
    private readonly ServiceProvider _provider;

    public TestRecorder(TimeProvider clock, PerformanceOptions? options = null)
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        Recorder = new RequestPerformanceRecorder(
            Options.Create(options ?? new PerformanceOptions()), clock, _provider.GetRequiredService<IMeterFactory>());
    }

    public RequestPerformanceRecorder Recorder { get; }

    public void Dispose()
    {
        Recorder.Dispose();
        _provider.Dispose();
    }
}
