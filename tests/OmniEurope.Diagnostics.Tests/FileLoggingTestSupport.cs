// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OmniEurope.Diagnostics.FileLogging;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>File loggers over a real folder, and their files read back as JSON.</summary>
internal static class FileLogs
{
    public static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A provider writing to <paramref name="directory"/>, its drain held until <paramref name="gate"/> completes.</summary>
    public static FileLoggerProvider Provider(
        string directory, Action<FileLoggingOptions>? configure = null, TimeProvider? time = null, Task? gate = null,
        Func<string, FileStream>? openFile = null)
    {
        var options = new FileLoggingOptions { Directory = directory };
        configure?.Invoke(options);
        return new FileLoggerProvider(options, directory, time ?? new FixedTime(Noon), gate ?? Task.CompletedTask, openFile);
    }

    /// <summary>Logs through a fresh provider, disposes it (which flushes) and returns the lines of its only file.</summary>
    public static JsonElement[] WriteAndRead(string directory, Action<ILogger> log, Action<FileLoggerProvider>? setup = null)
    {
        using (var provider = Provider(directory))
        {
            setup?.Invoke(provider);
            log(provider.CreateLogger("Shop.Orders"));
        }
        return Entries(Path.Combine(directory, "app-20261108.json"));
    }

    /// <summary>The lines of <paramref name="path"/>, read while a writer may hold it open.</summary>
    public static string[] Lines(string path)
    {
        if (!File.Exists(path))
            return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public static JsonElement[] Entries(string path) =>
        [.. Lines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    public static string? Text(this JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) ? value.ToString() : null;

    /// <summary>Waits, at most five seconds, for the background writer to reach <paramref name="condition"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The writer did not reach the expected state in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// An opener whose first file fails on flush or on dispose, as a disk that refuses the buffered lines would;
    /// the files opened after it are real.
    /// </summary>
    public static Func<string, FileStream> FirstFileFails(bool onFlush, bool onDispose)
    {
        var opened = 0;
        return path => Interlocked.Increment(ref opened) == 1
            ? new FailingFileStream(path, onFlush, onDispose)
            : new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    /// <summary>An opener of real files that counts how many times their buffers are flushed.</summary>
    internal sealed class FlushCounter
    {
        private int _flushes;

        public int Flushes => Volatile.Read(ref _flushes);

        public FileStream Open(string path) => new CountingFileStream(path, () => Interlocked.Increment(ref _flushes));
    }

    /// <summary>An opener whose first <paramref name="failures"/> openings throw, as a folder that is unwritable for a while; the next ones are real.</summary>
    public static Func<string, FileStream> FirstOpeningsFail(int failures)
    {
        var opened = 0;
        return path => Interlocked.Increment(ref opened) <= failures
            ? throw new IOException("The file cannot be opened.")
            : new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    private sealed class CountingFileStream(string path, Action onFlush)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
    {
        public override void Flush()
        {
            onFlush();
            base.Flush();
        }
    }

    private sealed class FailingFileStream(string path, bool onFlush, bool onDispose)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
    {
        public override void Flush()
        {
            if (onFlush)
                throw new IOException("The disk refused the flush.");
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && onDispose)
                throw new IOException("The disk refused the final flush.");
        }
    }
}
