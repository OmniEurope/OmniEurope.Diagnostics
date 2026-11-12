// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// One operation being timed, from <see cref="RequestPerformanceRecorder.StartOperation"/> to <see cref="Dispose"/>,
/// on the recorder's clock. Disposing records it once; a second dispose records nothing.
/// </summary>
public sealed class OperationTiming : IDisposable
{
    private readonly RequestPerformanceRecorder _recorder;
    private readonly long _startedAt;
    private int _ended;
    private volatile bool _failed;

    internal OperationTiming(RequestPerformanceRecorder recorder, string name, long startedAt)
    {
        _recorder = recorder;
        Name = name;
        _startedAt = startedAt;
    }

    /// <summary>The operation's name, as given to <see cref="RequestPerformanceRecorder.StartOperation"/>.</summary>
    public string Name { get; }

    /// <summary>Marks the operation failed: it is recorded so when the scope is disposed.</summary>
    public void Fail() => _failed = true;

    /// <summary>Records the operation, with the time elapsed since it started; only the first call records.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _recorder.RecordElapsed(Name, _startedAt, !_failed);
    }
}
