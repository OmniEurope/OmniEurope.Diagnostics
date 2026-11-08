// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>A formatted line and the instant of its entry, which picks the day's file.</summary>
internal readonly record struct QueuedLine(DateTimeOffset Timestamp, string Json);

/// <summary>
/// The file side of the logger: a bounded queue drained by one background task into the day's file, rolled to the
/// next numbered file above the size cap, plus the hourly deletion of the old files. Enqueuing never blocks and a
/// write failure never reaches the caller: what could not be queued or written is counted, then reported by one
/// warning line once a write succeeds again.
/// </summary>
internal sealed class LogFileWriter : IDisposable
{
    /// <summary>The <c>SourceContext</c> of the warning that reports dropped entries.</summary>
    internal static readonly string SourceContext = typeof(FileLoggerProvider).FullName!;

    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<QueuedLine> _queue;
    private readonly string _directory;
    private readonly LogFileNames _names;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly Task _drain;
    private long _dropped;
    private int _disposed;
    private FileStream? _stream;
    private DateOnly _day;
    private int _index;
    private DateTimeOffset? _lastRetention;

    public LogFileWriter(string directory, LogFileNames names, FileLoggingOptions options, TimeProvider time, Task drainGate)
    {
        _directory = directory;
        _names = names;
        _maxFileBytes = options.MaxFileBytes;
        _retentionDays = options.RetentionDays;
        _time = time;
        _queue = Channel.CreateBounded<QueuedLine>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _drain = Task.Run(() => DrainAsync(drainGate));
    }

    /// <summary>Queues <paramref name="json"/> without waiting; a full or closed queue counts it as dropped.</summary>
    public void Enqueue(DateTimeOffset timestamp, string json)
    {
        if (!_queue.Writer.TryWrite(new QueuedLine(timestamp, json)))
            CountDropped();
    }

    /// <summary>Counts an entry that will never be written.</summary>
    public void CountDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>Closes the queue and waits, at most five seconds, for the queued lines to be written.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _queue.Writer.TryComplete();
        _drain.Wait(DisposeTimeout);
    }

    private async Task DrainAsync(Task gate)
    {
        await gate.ConfigureAwait(false);
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    if (!TryWrite(line))
                        CountDropped();
                }
                ReportDropped();
                TryFlush();
                ApplyRetention();
            }
        }
        finally
        {
            CloseFile();
        }
    }

    private void ReportDropped()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped == 0)
            return;
        const string Reason = " log entries were dropped: the write queue was full or the log file could not be written.";
        var now = _time.GetUtcNow();
        var state = new KeyValuePair<string, object?>[] { new("DroppedCount", dropped), new("{OriginalFormat}", "{DroppedCount}" + Reason) };
        var message = dropped.ToString(System.Globalization.CultureInfo.InvariantCulture) + Reason;
        var json = ClefFormatter.Format(new ClefEvent(now, LogLevel.Warning, SourceContext, default, message, null), state, null, null);
        // Still unwritable: the count waits for the next write that succeeds.
        if (!TryWrite(new QueuedLine(now, json)))
            Interlocked.Add(ref _dropped, dropped);
    }

    private bool TryWrite(QueuedLine line)
    {
        try
        {
            var bytes = Utf8.GetBytes(line.Json + "\n");
            StreamFor(DateOnly.FromDateTime(line.Timestamp.UtcDateTime), bytes.Length).Write(bytes);
            return true;
        }
        catch (Exception)
        {
            // A full disk, a missing permission, a folder turned into a file: the logger never fails the
            // application; the file is reopened on the next line.
            CloseFile();
            return false;
        }
    }

    private FileStream StreamFor(DateOnly day, int length)
    {
        if (_stream is null || day != _day)
        {
            CloseFile();
            _day = day;
            _index = 0;
            Directory.CreateDirectory(_directory);
            _stream = Open();
        }
        // A line never splits: a file that cannot take it whole is continued in the next one, unless it is empty.
        while (_stream.Length > 0 && _stream.Length + length > _maxFileBytes)
        {
            _stream.Dispose();
            _index++;
            _stream = Open();
        }
        return _stream;
    }

    // Shared for reading and writing, so the logs page reads the file while it is written.
    private FileStream Open() =>
        new(Path.Combine(_directory, _names.For(_day, _index)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

    private void TryFlush()
    {
        try
        {
            _stream?.Flush();
        }
        catch (Exception)
        {
            // The buffered lines are lost with the file handle; the next line reopens the file.
            CloseFile();
        }
    }

    private void CloseFile()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Disposing flushes: a disk that refuses it loses those lines, never the application.
        }
        _stream = null;
    }

    private void ApplyRetention()
    {
        var now = _time.GetUtcNow();
        if (_retentionDays == 0 || now - _lastRetention < RetentionPeriod)
            return;
        _lastRetention = now;
        var oldest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_retentionDays);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, _names.Prefix + "*.json"))
            {
                if (_names.TryGetDay(Path.GetFileName(path), out var day) && day < oldest)
                    TryDelete(path);
            }
        }
        catch (Exception)
        {
            // The folder is missing or unreadable: the next pass, an hour later, tries again.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Opened by a reader or read-only: kept until the next pass.
        }
    }
}
