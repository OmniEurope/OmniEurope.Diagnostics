// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics;

/// <summary>
/// Measures, in process, the requests of an ASP.NET Core host and the operations any host times itself, with no
/// middleware on the request path. Requests are heard on the request-duration histogram ASP.NET Core already
/// publishes (<c>Microsoft.AspNetCore.Hosting</c> / <c>http.server.request.duration</c>), and only when it was
/// created by this host's <see cref="IMeterFactory"/>, so two hosts in one process never mix their figures.
/// Operations are recorded directly (<see cref="RecordOperation"/>, <see cref="StartOperation"/>) or heard on the
/// host's own histograms named in <see cref="PerformanceOptions.Instruments"/>; their convention on a sample is
/// described on <see cref="RequestTimingSample"/>. Keeps the window's slowest samples (<see cref="SlowestRequests"/>)
/// and, in the rest of <see cref="PerformanceOptions.MemoryLimitBytes"/>, a rolling ring of samples for the
/// per-route percentiles (<see cref="SampleRing"/>); requests and operations share both.
///
/// The window lives in this process's memory and restarts with it; <see cref="RequestPerformanceSummary.WindowStart"/>
/// and <see cref="RequestPerformanceSummary.Since"/> state since when the figures count.
/// </summary>
public sealed class RequestPerformanceRecorder : IHostedService, IDisposable
{
    /// <summary>The meter ASP.NET Core publishes its request metrics on.</summary>
    public const string MeterName = "Microsoft.AspNetCore.Hosting";

    /// <summary>The request-duration histogram, in seconds.</summary>
    public const string InstrumentName = "http.server.request.duration";

    /// <summary>What the recorder holds besides the ring and the slowest buckets (itself, its options, its
    /// dictionaries, the route cache of a typical site), set aside from the memory limit.</summary>
    internal const int FixedOverheadBytes = 32 * 1024;

    /// <summary>Route families left out by default, matched as whole segments: <c>/health</c> and <c>/health/ready</c>,
    /// never <c>/healthcare</c>. <c>/favicon.ico</c> and <c>/robots.txt</c> fall under the static-file rule.</summary>
    private static readonly string[] ExcludedFamilies = ["/health", "/_framework", "/_content", "/_blazor"];

    private readonly SampleRing _samples;
    private readonly ConcurrentDictionary<string, string?> _templates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _operations = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly PerformanceOptions _options;
    private readonly IMeterFactory _meterScope;
    private readonly SlowestRequests _slowest;
    private readonly DateTime _startedAt;
    private readonly Func<string, bool> _filter;
    private readonly HostInstruments _instruments;
    // Raised by a recorded sample, taken by WaitForChangeAsync; never disposed: it holds no wait handle,
    // and a listener callback racing the host's shutdown must not hit a disposed semaphore on the request path.
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Lock _gate = new();
    private MeterListener? _listener;
    private bool _disposed;

    /// <summary>Creates the recorder; <see cref="Start"/> (or <see cref="StartAsync"/>, called by the host) starts
    /// listening. <see cref="RecordOperation"/> and <see cref="StartOperation"/> work without it.</summary>
    /// <param name="options">The window, the slowest count, the memory limit, the routes measured and the host's
    /// own instruments.</param>
    /// <param name="time">The clock that dates the samples, ages them out and times <see cref="StartOperation"/>.</param>
    /// <param name="meterFactory">The host's meter factory: only its request instruments are heard.</param>
    /// <exception cref="ArgumentOutOfRangeException">A window or a slowest count that is not positive, a window
    /// reaching back before the first calendar date, or a memory limit that leaves no room for the rolling samples
    /// once the slowest list has its share.</exception>
    /// <exception cref="ArgumentException">A host instrument without its meter name, its instrument name or its name tag.</exception>
    public RequestPerformanceRecorder(IOptions<PerformanceOptions> options, TimeProvider time, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(meterFactory);
        _options = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Window, TimeSpan.Zero, "options.Window");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.SlowestCount, 1, "options.SlowestCount");
        // Compare before each subtraction; even a saturated reservation must never wrap into a usable budget.
        var availableBytes = _options.MemoryLimitBytes > FixedOverheadBytes ? _options.MemoryLimitBytes - FixedOverheadBytes : 0;
        var reservedBytes = SlowestRequests.ReservedBytes(_options.SlowestCount, _options.Window);
        var capacity = availableBytes > reservedBytes
            ? (availableBytes - reservedBytes) / Unsafe.SizeOf<RequestTimingSample>()
            : 0;
        if (capacity < 1)
            throw new ArgumentOutOfRangeException("options.MemoryLimitBytes", _options.MemoryLimitBytes,
                "The memory limit leaves no room for the rolling samples: raise it, or lower SlowestCount or Window.");
        // Every read and every new hour subtract the window from the clock: past the calendar, they would throw.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Window, now - DateTime.MinValue, "options.Window");
        _instruments = new HostInstruments(_options.Instruments);
        _time = time;
        _meterScope = meterFactory;
        _slowest = new SlowestRequests(_options.SlowestCount, _options.Window);
        _samples = new SampleRing((int)Math.Min(capacity, Array.MaxLength));
        _startedAt = now;
        _filter = _options.RouteFilter ?? IsMeasuredByDefault;
    }

    /// <summary>
    /// Starts listening to the ASP.NET Core request histogram and to the host's instruments. The host calls it
    /// through <see cref="StartAsync"/>; a host that runs no hosted service (a .NET MAUI application) calls it
    /// once its services are built. Idempotent: a second call while listening does nothing.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The recorder was disposed.</exception>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            var listener = new MeterListener { InstrumentPublished = OnInstrumentPublished };
            listener.SetMeasurementEventCallback<double>(OnMeasurement);
            listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener = listener;
            listener.Start();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopListening();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        StopListening();
    }

    /// <summary>The figures of the last <see cref="PerformanceOptions.Window"/>: the slowest calls and every route's percentiles.</summary>
    public RequestPerformanceSummary Summarize()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _options.Window;
        var (samples, truncated) = _samples.Since(cutoff);
        return RequestPerformanceReport.Build(samples, truncated, _slowest.Since(cutoff), cutoff > _startedAt ? cutoff : _startedAt);
    }

    /// <summary>
    /// Completes once a request or an operation has been recorded since the previous wait returned: any number of
    /// samples in between collapse into one signal, and a route the filter leaves out raises none. Meant for one
    /// consumer that then tells the readers (a hub broadcast, a cache refresh), spacing its reads as it sees fit:
    /// two concurrent waiters share the signals, each one wakes a single waiter.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait, with an <see cref="OperationCanceledException"/>.</param>
    public Task WaitForChangeAsync(CancellationToken cancellationToken) => _changed.WaitAsync(cancellationToken);

    /// <summary>
    /// Records one operation the host timed itself (reading a file, loading a screen), whether or not the recorder
    /// was started. It shares the window, the slowest list, the memory limit and the change signal with the
    /// requests, and is filed under its name as given, trimmed: <see cref="PerformanceOptions.RouteFilter"/> does
    /// not apply and the case is kept. The name is a family, like a route template (<c>Import file</c>), never an
    /// identifier: each distinct name is kept for the process's life, outside the memory limit.
    /// </summary>
    /// <param name="name">The operation's name.</param>
    /// <param name="duration">How long it took.</param>
    /// <param name="succeeded">False when it failed.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public void RecordOperation(string name, TimeSpan duration, bool succeeded = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        AddOperation(name, duration.TotalMilliseconds, succeeded);
    }

    /// <summary>
    /// Starts timing one operation on the recorder's clock; disposing the returned scope records it once, as
    /// <see cref="RecordOperation"/> would, failed if <see cref="OperationTiming.Fail"/> was called:
    /// <code>
    /// using var timing = recorder.StartOperation("Import file");
    /// try { Import(); }
    /// catch { timing.Fail(); throw; }
    /// </code>
    /// </summary>
    /// <param name="name">The operation's name, as for <see cref="RecordOperation"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    public OperationTiming StartOperation(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new OperationTiming(this, name, _time.GetTimestamp());
    }

    /// <summary>
    /// The default filter: every templated route but the health probes, the framework's own endpoints and
    /// static files (a last segment holding a dot and no parameter, <c>/css/app.css</c>). A family is matched
    /// by whole segment, so <c>/healthcare/{id}</c> stays measured.
    /// </summary>
    /// <param name="route">The route template, lower case with a leading slash.</param>
    public static bool IsMeasuredByDefault(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        foreach (var family in ExcludedFamilies)
        {
            if (route.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                && (route.Length == family.Length || route[family.Length] == '/'))
            {
                return false;
            }
        }
        var lastSegment = route[(route.LastIndexOf('/') + 1)..];
        return !(lastSegment.Contains('.', StringComparison.Ordinal) && !lastSegment.Contains('{', StringComparison.Ordinal));
    }

    /// <summary>Records the operation a scope timed, from its start timestamp.</summary>
    internal void RecordElapsed(string name, long startedAt, bool succeeded) =>
        AddOperation(name, _time.GetElapsedTime(startedAt).TotalMilliseconds, succeeded);

    /// <summary>Adds one request; the entry point of the listener, internal so tests can feed it.</summary>
    internal void Record(string? route, string method, int statusCode, double durationMs)
    {
        // No template means no endpoint matched (a 404, a file served by middleware): nothing to group it under.
        if (route is null) return;

        if (Template(route) is not { } template) return;

        Add(new RequestTimingSample(_time.GetUtcNow().UtcDateTime, method, template, statusCode, durationMs));
    }

    private void StopListening()
    {
        lock (_gate)
        {
            _listener?.Dispose();
            _listener = null;
        }
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name == MeterName && instrument.Name == InstrumentName)
        {
            // The request histogram of another host in the same process is never heard.
            if (ReferenceEquals(instrument.Meter.Scope, _meterScope))
                listener.EnableMeasurementEvents(instrument);
            return;
        }

        if (_instruments.Bind(instrument) is { } binding)
            listener.EnableMeasurementEvents(instrument, binding);
    }

    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is HostInstrumentBinding binding)
        {
            OnOperation(binding, value, tags);
            return;
        }

        string? route = null;
        var method = string.Empty;
        var status = 0;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "http.route": route = tag.Value as string; break;
                case "http.request.method": method = tag.Value as string ?? string.Empty; break;
                case "http.response.status_code": status = tag.Value is int code ? code : 0; break;
            }
        }

        Record(route, method, status, value * 1000);
    }

    // Only host instruments publish long measurements to this listener: the request histogram is a double one.
    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        OnOperation((HostInstrumentBinding)state!, value, tags);

    private void OnOperation(HostInstrumentBinding binding, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (binding.Read(value, tags) is { } operation)
            AddOperation(operation.Name, operation.DurationMs, operation.Succeeded);
    }

    private void AddOperation(string name, double durationMs, bool succeeded)
    {
        // One string per name, shared by its samples, as the route templates are.
        var operation = _operations.GetOrAdd(name, static raw => raw.Trim());
        Add(new RequestTimingSample(_time.GetUtcNow().UtcDateTime, string.Empty, operation,
            succeeded ? RequestTimingSample.OperationSucceeded : RequestTimingSample.OperationFailed, durationMs));
    }

    private void Add(RequestTimingSample sample)
    {
        _slowest.Add(sample);
        _samples.Add(sample);
        SignalChange();
    }

    private void SignalChange()
    {
        // A binary signal: already raised means the waiter will read this sample with the others.
        if (_changed.CurrentCount > 0) return;
        try { _changed.Release(); }
        catch (SemaphoreFullException) { } // two samples raced to raise the same signal
    }

    /// <summary>
    /// The route template a sample is filed under, or null when the filter leaves it out. ASP.NET Core writes
    /// a template in the case of its source (api/Orders from a controller class, /api/orders from an
    /// attribute) and routes both alike: one family, one line. Decided once per template, and every sample
    /// of a route shares one string, so the samples hold no string of their own (the templates are the
    /// host's routes, a bounded set kept outside the memory limit). A static factory: the lookup of a known
    /// route allocates nothing.
    /// </summary>
    private string? Template(string route) => _templates.GetOrAdd(route, static (raw, filter) =>
    {
        var template = "/" + raw.Trim('/').ToLowerInvariant();
        return filter(template) ? template : null;
    }, _filter);
}
