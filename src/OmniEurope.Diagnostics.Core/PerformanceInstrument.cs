// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// A histogram of the host's own, listed in <see cref="PerformanceOptions.Instruments"/>: each of its measurements
/// is recorded as an operation (see <see cref="RequestTimingSample"/>), named by the value of its
/// <see cref="NameTag"/> tag.
/// </summary>
/// <remarks>
/// <para>The histogram is heard whatever its meter's scope: a meter created with <c>new Meter(name)</c> has no scope
/// and one created by an <c>IMeterFactory</c> has that factory as scope, both are heard. Two recorders in one
/// process naming the same meter therefore both hear it.</para>
/// <para>Its measurements may be <see cref="double"/> or <see cref="long"/>, and its unit, read from the
/// instrument, says what they count: <c>s</c> for seconds, <c>ms</c> for milliseconds. A histogram with any other
/// unit, or none, is not heard: guessing its scale would show wrong durations. A measurement without the name tag,
/// with an empty name, or with a negative or non-finite value is ignored.</para>
/// <para>An operation failed when its measurement carries an <c>error.type</c> tag with a value (the OpenTelemetry
/// convention), or an <c>outcome</c> tag equal to <c>failure</c> or <c>error</c> in any case; otherwise it
/// succeeded.</para>
/// </remarks>
public sealed class PerformanceInstrument
{
    /// <summary>The default <see cref="NameTag"/>.</summary>
    public const string DefaultNameTag = "operation";

    /// <summary>Required: the meter's name, <c>MyApp.Import</c> for instance.</summary>
    public string MeterName { get; set; } = "";

    /// <summary>Required: the histogram's name, <c>myapp.import.duration</c> for instance.</summary>
    public string InstrumentName { get; set; } = "";

    /// <summary>The tag whose value names the operation: <see cref="DefaultNameTag"/> by default.</summary>
    public string NameTag { get; set; } = DefaultNameTag;
}
