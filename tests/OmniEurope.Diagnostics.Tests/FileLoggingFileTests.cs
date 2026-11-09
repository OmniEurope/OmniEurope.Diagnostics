// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;
using OmniEurope.Diagnostics.FileLogging;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The files the logger writes in a real folder: names, roll-over, retention, the queue and its flush.</summary>
public sealed class FileLoggingFileTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void EachUtcDay_HasItsOwnFile_PickedByTheEntrysTime()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 8, 23, 59, 59, TimeSpan.Zero));
        using (var provider = FileLogs.Provider(_folder.Path, time: clock))
        {
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation("before midnight");
            clock.Advance(TimeSpan.FromSeconds(2));
            logger.LogInformation("after midnight");
        }

        Assert.Equal(["before midnight"], FileLogs.Entries(_folder.File("app-20261108.json")).Select(entry => entry.Text("@m")));
        Assert.Equal(["after midnight"], FileLogs.Entries(_folder.File("app-20261109.json")).Select(entry => entry.Text("@m")));
    }

    [Fact]
    public void AFullFile_IsContinuedInTheNextNumberedOne_AndARestartResumesTheLastWithRoom()
    {
        // About 1.25 KB a line (the template and the message): three lines a 4 KiB file.
        var message = new string('x', 600);
        using (var provider = FileLogs.Provider(_folder.Path, options => options.MaxFileBytes = FileLoggingOptions.MinMaxFileBytes))
        {
            var logger = provider.CreateLogger("Cat");
            for (var i = 0; i < 10; i++)
                logger.LogInformation(message);
        }
        using (var provider = FileLogs.Provider(_folder.Path, options => options.MaxFileBytes = FileLoggingOptions.MinMaxFileBytes))
            provider.CreateLogger("Cat").LogInformation("after restart " + message);

        string[] names = ["app-20261108.json", "app-20261108.1.json", "app-20261108.2.json", "app-20261108.3.json"];
        Assert.Equal(names.Order(StringComparer.Ordinal), Directory.EnumerateFiles(_folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(names, name => Assert.InRange(new FileInfo(_folder.File(name)).Length, 1, FileLoggingOptions.MinMaxFileBytes));
        Assert.Equal(11, names.Sum(name => FileLogs.Lines(_folder.File(name)).Length));
        Assert.Equal("after restart " + message, FileLogs.Entries(_folder.File(names[^1]))[^1].Text("@m"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" .-. ", null)]
    [InlineData("web-01", "web-01")]
    [InlineData("Host.Example_1", "Host.Example_1")]
    [InlineData(" web 01/é\\..", "web-01")]
    public void TheInstance_IsReducedToAHostNameLikeToken(string? instance, string? expected) =>
        Assert.Equal(expected, LogFileNames.SanitizeInstance(instance));

    [Fact]
    public void ALongInstance_IsCut_WithoutATrailingSeparator()
    {
        Assert.Equal(new string('a', 64), LogFileNames.SanitizeInstance(new string('a', 100)));
        Assert.Equal(new string('a', 63), LogFileNames.SanitizeInstance(new string('a', 63) + "-b" + new string('c', 10)));
    }

    [Fact]
    public void TheInstance_GoesBetweenThePrefixAndTheDay()
    {
        using (var provider = FileLogs.Provider(_folder.Path, options => (options.FilePrefix, options.Instance) = ("shop", "web 01")))
            provider.CreateLogger("Cat").LogInformation("m");

        Assert.Equal(["shop-web-01-20261108.json"], Directory.EnumerateFiles(_folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Retention_DeletesOnlyThisPrefixsFilesOlderThanTheKeptDays()
    {
        string[] deleted = ["app-20261001.json", "app-web-01-20261001.1.json", "app-20261008.json"];
        string[] kept = ["app-20261009.json", "app-20261020.json", "application-20200101.json", "other-20200101.json", "app-20261399.json", "app-old.json", "app-20200101.txt"];
        foreach (var name in deleted.Concat(kept))
            _folder.Write(name, ["{}"]);

        using (var provider = FileLogs.Provider(_folder.Path))
            provider.CreateLogger("Cat").LogInformation("m");

        Assert.Equal(kept.Append("app-20261108.json").Order(StringComparer.Ordinal), Directory.EnumerateFiles(_folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ZeroRetentionDays_NeverDeletes()
    {
        _folder.Write("app-20000101.json", ["{}"]);

        using (var provider = FileLogs.Provider(_folder.Path, options => options.RetentionDays = 0))
            provider.CreateLogger("Cat").LogInformation("m");

        Assert.True(File.Exists(_folder.File("app-20000101.json")));
    }

    [Fact]
    public async Task Retention_RunsAtMostOnceAnHour_AndTheFileIsReadableWhileWritten()
    {
        var clock = new ManualClock(FileLogs.Noon);
        var today = _folder.File("app-20261108.json");
        using var provider = FileLogs.Provider(_folder.Path, time: clock);
        var logger = provider.CreateLogger("Cat");
        logger.LogInformation("first");
        await FileLogs.WaitUntilAsync(() => FileLogs.Lines(today).Length == 1);

        var old = _folder.Write("app-20000101.json", ["{}"]);
        clock.Advance(TimeSpan.FromMinutes(59));
        logger.LogInformation("second");
        await FileLogs.WaitUntilAsync(() => FileLogs.Lines(today).Length == 2);
        Assert.True(File.Exists(old));

        clock.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("third");
        await FileLogs.WaitUntilAsync(() => !File.Exists(old));
    }

    [Fact]
    public void ASaturatedQueue_DropsAndCounts_ThenWritesOneWarning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var provider = FileLogs.Provider(_folder.Path, options => options.QueueCapacity = FileLoggingOptions.MinQueueCapacity, gate: gate.Task))
        {
            var logger = provider.CreateLogger("Cat");
            for (var i = 0; i < FileLoggingOptions.MinQueueCapacity + 4; i++)
                logger.LogInformation("Entry {Number}", i);
            gate.SetResult();
        }

        var entries = FileLogs.Entries(_folder.File("app-20261108.json"));
        Assert.Equal(FileLoggingOptions.MinQueueCapacity + 1, entries.Length);
        Assert.Equal(Enumerable.Range(0, FileLoggingOptions.MinQueueCapacity).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            entries[..^1].Select(entry => entry.Text("Number")));
        var warning = entries[^1];
        Assert.Equal(("Warning", "OmniEurope.Diagnostics.FileLogging.FileLoggerProvider", "4"), (warning.Text("@l"), warning.Text("SourceContext"), warning.Text("DroppedCount")));
        Assert.Equal("{DroppedCount} log entries were dropped: the write queue was full or the log file could not be written.", warning.Text("@mt"));
        Assert.StartsWith("4 log entries were dropped", warning.Text("@m"), StringComparison.Ordinal);
    }

    [Fact]
    public void LinesLostByAFailedFlush_AreCounted_AndReportedByTheWarning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var provider = FileLogs.Provider(_folder.Path, gate: gate.Task, openFile: FileLogs.FirstFileFails(onFlush: true, onDispose: false)))
        {
            var logger = provider.CreateLogger("Cat");
            for (var i = 0; i < 3; i++)
                logger.LogInformation("Entry {Number}", i);
            gate.SetResult();
        }

        var warning = Assert.Single(FileLogs.Entries(_folder.File("app-20261108.json")), entry => entry.Text("SourceContext") == LogFileWriter.SourceContext);
        Assert.Equal(("Warning", "3"), (warning.Text("@l"), warning.Text("DroppedCount")));
    }

    [Fact]
    public void LinesLostByAFailedDispose_AreCounted_AndReportedByTheWarning()
    {
        var clock = new ManualClock(FileLogs.Noon);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var provider = FileLogs.Provider(_folder.Path, time: clock, gate: gate.Task, openFile: FileLogs.FirstFileFails(onFlush: false, onDispose: true)))
        {
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation("first day {Number}", 1);
            logger.LogInformation("first day {Number}", 2);
            clock.Advance(TimeSpan.FromHours(13));
            logger.LogInformation("second day {Number}", 3);
            gate.SetResult();
        }

        // The first day's file is closed when the second day's first line arrives: its close fails, its two lines are lost.
        var entries = FileLogs.Entries(_folder.File("app-20261109.json"));
        Assert.Equal(["second day 3", "2 log entries were dropped: the write queue was full or the log file could not be written."], entries.Select(entry => entry.Text("@m")));
        Assert.Equal("2", entries[^1].Text("DroppedCount"));
    }

    [Fact]
    public void Dispose_FlushesEveryQueuedEntry_IsIdempotent_AndLaterEntriesAreIgnored()
    {
        var provider = FileLogs.Provider(_folder.Path);
        var logger = provider.CreateLogger("Cat");
        for (var i = 0; i < 500; i++)
            logger.LogInformation("Entry {Number}", i);

        provider.Dispose();
        provider.Dispose();
        logger.LogInformation("after dispose");

        Assert.Equal(500, FileLogs.Lines(_folder.File("app-20261108.json")).Length);
    }

    [Fact]
    public void AnUnwritableFolder_NeverThrowsIntoTheApplication()
    {
        var blocker = _folder.Write("blocked", ["not a folder"]);

        var provider = FileLogs.Provider(blocker);
        provider.CreateLogger("Cat").LogError(new InvalidOperationException("boom"), "lost");
        provider.Dispose();

        Assert.Equal(["not a folder"], File.ReadAllLines(blocker));
    }
}
