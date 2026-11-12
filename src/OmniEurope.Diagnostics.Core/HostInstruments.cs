// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;

namespace OmniEurope.Diagnostics;

/// <summary>The host instruments of <see cref="PerformanceOptions.Instruments"/>, copied when the recorder is
/// created, so a later change of the options never alters what it listens to.</summary>
internal sealed class HostInstruments
{
    private readonly (string Meter, string Instrument, string NameTag)[] _instruments;

    /// <exception cref="ArgumentException">An instrument that is null or misses its meter name, its instrument name
    /// or its name tag.</exception>
    public HostInstruments(IEnumerable<PerformanceInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        _instruments = [.. instruments.Select(static instrument =>
            instrument is null
            || string.IsNullOrWhiteSpace(instrument.MeterName)
            || string.IsNullOrWhiteSpace(instrument.InstrumentName)
            || string.IsNullOrWhiteSpace(instrument.NameTag)
                ? throw new ArgumentException(
                    "Every host instrument names its meter, its histogram and the tag carrying the operation name.", "options.Instruments")
                : (instrument.MeterName, instrument.InstrumentName, instrument.NameTag))];
    }

    /// <summary>How the listener reads <paramref name="instrument"/>, or null when it is not one of the host's
    /// histograms or its unit is neither <c>s</c> nor <c>ms</c>.</summary>
    public HostInstrumentBinding? Bind(Instrument instrument)
    {
        if (instrument is not (Histogram<double> or Histogram<long>)) return null;
        double? millisecondsPerUnit = instrument.Unit switch
        {
            "s" => 1000,
            "ms" => 1,
            _ => null,
        };
        if (millisecondsPerUnit is not { } scale) return null;
        foreach (var (meter, name, nameTag) in _instruments)
        {
            if (string.Equals(instrument.Meter.Name, meter, StringComparison.Ordinal)
                && string.Equals(instrument.Name, name, StringComparison.Ordinal))
            {
                return new HostInstrumentBinding(nameTag, scale);
            }
        }
        return null;
    }
}

/// <summary>One heard host histogram: the tag naming its operations and the milliseconds one unit is worth.</summary>
internal sealed record HostInstrumentBinding(string NameTag, double MillisecondsPerUnit)
{
    /// <summary>The operation one measurement records, or null when it names none or its value is unusable.</summary>
    public (string Name, double DurationMs, bool Succeeded)? Read(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? name = null;
        var failed = false;
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, NameTag, StringComparison.Ordinal))
                name = tag.Value as string;
            else if (string.Equals(tag.Key, "error.type", StringComparison.Ordinal))
                failed |= tag.Value is not null;
            else if (string.Equals(tag.Key, "outcome", StringComparison.Ordinal))
                failed |= tag.Value is string outcome && IsFailure(outcome);
        }

        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(value) || value < 0) return null;
        return (name, value * MillisecondsPerUnit, !failed);
    }

    private static bool IsFailure(string outcome) =>
        outcome.Equals("failure", StringComparison.OrdinalIgnoreCase) || outcome.Equals("error", StringComparison.OrdinalIgnoreCase);
}
