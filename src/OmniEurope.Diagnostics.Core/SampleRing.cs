// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// The requests the per-route percentiles are computed from, in a ring allocated once at its full size:
/// its memory is known up front and never grows. When it is full, the newest request takes the oldest
/// one's place.
/// </summary>
/// <remarks>
/// Requests are dated before they reach the ring, so two simultaneous ones may arrive out of order: the
/// ring is ordered by arrival, not by date. Reads therefore filter every entry, and the overwritten date
/// only ever moves forward.
/// </remarks>
internal sealed class SampleRing(int capacity)
{
    private readonly RequestTimingSample[] _items = new RequestTimingSample[capacity];
    private readonly Lock _gate = new();
    private int _head;
    private int _count;
    private long _lastOverwrittenTicks = long.MinValue;

    public void Add(RequestTimingSample sample)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_head + _count) % _items.Length] = sample;
                _count++;
                return;
            }
            _lastOverwrittenTicks = Math.Max(_lastOverwrittenTicks, _items[_head].At.Ticks);
            _items[_head] = sample;
            _head = (_head + 1) % _items.Length;
        }
    }

    /// <summary>The requests at or after <paramref name="cutoff"/>, in arrival order, forgetting the expired
    /// ones at the head; and whether the ring overwrote a request that would still be inside the window.</summary>
    public (RequestTimingSample[] Samples, bool Truncated) Since(DateTime cutoff)
    {
        lock (_gate)
        {
            while (_count > 0 && _items[_head].At < cutoff)
            {
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                _count--;
            }
            var kept = 0;
            for (var i = 0; i < _count; i++)
            {
                if (_items[(_head + i) % _items.Length].At >= cutoff) kept++;
            }
            var samples = new RequestTimingSample[kept];
            var next = 0;
            for (var i = 0; i < _count; i++)
            {
                var sample = _items[(_head + i) % _items.Length];
                if (sample.At >= cutoff) samples[next++] = sample;
            }
            return (samples, _lastOverwrittenTicks >= cutoff.Ticks);
        }
    }
}
