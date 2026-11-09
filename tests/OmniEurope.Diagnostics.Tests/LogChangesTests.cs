// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The change signal: throttled, filtered to the log files, lazy, and patient with a missing folder.</summary>
public sealed class LogChangesTests : IDisposable
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(ILogChanges.ThrottleMilliseconds);
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(ILogChanges.RetryMilliseconds);
    private readonly TempLogFolder _folder = new();
    private readonly TimerClock _clock = new(Clef.Noon);
    private readonly List<ServiceProvider> _providers = [];
    private int _raised;

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _folder.Dispose();
    }

    private LogChangeWatcher Changes(string? directory = null, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniLogs(options => options.Directory = directory ?? _folder.Path);
        builder.Services.AddSingleton(time ?? _clock);
        var provider = builder.Services.BuildServiceProvider();
        _providers.Add(provider);
        return (LogChangeWatcher)provider.GetRequiredService<ILogChanges>();
    }

    private void Count(object? sender, EventArgs e) => Interlocked.Increment(ref _raised);

    [Fact]
    public void ABurst_RaisesOnceAtOnce_ThenOnceAtTheEndOfThePeriod_AndAQuietPeriodRaisesAtOnceAgain()
    {
        var changes = Changes();
        changes.Changed += Count;

        changes.OnFileEvent("app.json");
        changes.OnFileEvent("app.json");
        changes.OnFileEvent("app-2.json");
        Assert.Equal(1, _raised);

        _clock.Advance(Throttle - TimeSpan.FromMilliseconds(1));
        changes.OnFileEvent("app.json");
        Assert.Equal(1, _raised);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, _raised);

        _clock.Advance(Throttle * 3);
        Assert.Equal(2, _raised);
        changes.OnFileEvent("app.json");
        Assert.Equal(3, _raised);
    }

    [Fact]
    public void AThrowingHandler_IsIgnored_AndTheOtherHandlersStillRun()
    {
        var changes = Changes();
        changes.Changed += (_, _) => throw new InvalidOperationException("subscriber failed");
        changes.Changed += Count;

        var exception = Record.Exception(() => changes.OnFileEvent("app.json"));

        Assert.Null(exception);
        Assert.Equal(1, _raised);
    }

    [Theory]
    [InlineData("omni-log-rules.json", null, false)]
    [InlineData("omni-log-rules.json.0a1b.tmp", null, false)]
    [InlineData("notes.txt", null, false)]
    [InlineData(null, null, false)]
    [InlineData("app.json", null, true)]
    [InlineData("APP.JSON", null, true)]
    [InlineData("app.json.old", "app.json", true)]
    [InlineData("app.json", "app.tmp", true)]
    public void OnlyALogFile_RaisesTheSignal_EitherNameOfARename(string? name, string? oldName, bool raised)
    {
        var changes = Changes();
        changes.Changed += Count;

        changes.OnFileEvent(name, oldName);

        Assert.Equal(raised ? 1 : 0, _raised);
    }

    [Fact]
    public void TheFolder_IsWatchedFromTheFirstSubscriptionToTheLast()
    {
        var changes = Changes();
        EventHandler other = (_, _) => { };

        Assert.False(changes.Watching);
        changes.Changed += Count;
        changes.Changed += other;
        Assert.True(changes.Watching);
        changes.Changed -= Count;
        Assert.True(changes.Watching);
        changes.Changed -= other;
        Assert.False(changes.Watching);
        changes.OnFileEvent("app.json");
        Assert.Equal(0, _raised);
    }

    [Fact]
    public void AMissingFolder_IsNeverCreated_AndIsWatchedOnceItAppears_WithOneSignal()
    {
        var folder = _folder.File("later");
        var changes = Changes(folder);
        changes.Changed += Count;

        Assert.False(changes.Watching);
        _clock.Advance(Retry);
        Assert.False(Directory.Exists(folder));
        Assert.False(changes.Watching);

        Directory.CreateDirectory(folder);
        _clock.Advance(Retry);

        Assert.True(changes.Watching);
        Assert.Equal(1, _raised);
        _clock.Advance(Retry * 3);
        Assert.Equal(1, _raised);
        Assert.Equal(0, _clock.LiveTimers);
    }

    [Fact]
    public void AFailedWatcher_IsReported_ThenWatchedAgain()
    {
        var changes = Changes();
        changes.Changed += Count;

        changes.OnWatcherError(new object()); // an older watcher, already replaced: the current one stays
        Assert.True(changes.Watching);
        changes.OnWatcherError();
        Assert.False(changes.Watching);
        Assert.Equal(1, _raised);

        _clock.Advance(Retry);

        Assert.True(changes.Watching);
        Assert.Equal(2, _raised);
    }

    [Fact]
    public void Unsubscribing_StopsTheTimers_AndDisposing_RefusesNewSubscribers()
    {
        var changes = Changes(_folder.File("absent"));
        changes.Changed += Count;
        changes.OnFileEvent("app.json");
        changes.OnFileEvent("app.json");
        Assert.Equal(2, _clock.LiveTimers); // the retry and the pending signal

        changes.Changed -= Count;
        Assert.Equal(0, _clock.LiveTimers);
        _clock.Advance(Retry);
        Assert.Equal(1, _raised);

        changes.Dispose();
        changes.Changed += Count;
        changes.Retry();
        Assert.False(changes.Watching);
        Assert.Equal(0, _clock.LiveTimers);
    }

    [Fact]
    public void TheSignal_IsOneSingleton_DisposedWithTheHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddOmniLogs(options => options.Directory = _folder.Path);
        LogChangeWatcher changes;
        using (var provider = builder.Services.BuildServiceProvider())
        {
            changes = (LogChangeWatcher)provider.GetRequiredService<ILogChanges>();
            Assert.Same(changes, provider.GetRequiredService<ILogChanges>());
            changes.Changed += Count;
            Assert.True(changes.Watching);
        }

        Assert.False(changes.Watching);
    }

    [Fact]
    public async Task ARealFileWritten_RaisesTheSignal()
    {
        var changes = Changes(time: TimeProvider.System);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changes.Changed += (_, _) => raised.TrySetResult();

        await File.WriteAllTextAsync(_folder.File("app-20261108.json"), Clef.Line(Clef.Noon, "written") + "\n", TestContext.Current.CancellationToken);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
