// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The write filter of the file logger: what it drops, what it sees, and a broken filter.</summary>
public sealed class FileLoggingFilterTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string[] Messages() =>
        [.. FileLogs.Entries(_folder.File("app-20261108.json")).Select(entry => entry.Text("@m")!)];

    [Fact]
    public void AnEntryTheFilterRefuses_IsNotWritten_NorCountedAsLost_AndTheFilterSeesTheEntry()
    {
        var seen = new List<FileLogCandidate>();
        using (var provider = FileLogs.Provider(_folder.Path, options => options.Filter = candidate =>
               {
                   seen.Add(candidate);
                   return candidate.Exception is not InvalidOperationException;
               }))
        {
            var logger = provider.CreateLogger("Hub.Errors");
            logger.LogError(new EventId(7, "HubFailed"), new InvalidOperationException("noise"), "Hub call {Name} failed", "Send");
            logger.LogWarning("Kept {Count}", 3);
        }

        // A refused entry is not a lost one: the file holds no warning about dropped entries either.
        Assert.Equal(["Kept 3"], Messages());
        var refused = seen[0];
        Assert.Equal(("Hub.Errors", LogLevel.Error, 7, "Hub call Send failed"), (refused.Category, refused.Level, refused.EventId.Id, refused.Message));
        Assert.IsType<InvalidOperationException>(refused.Exception);
    }

    [Fact]
    public void AnEntryBelowTheLevel_NeverReachesTheFilter()
    {
        var calls = 0;
        using (var provider = FileLogs.Provider(_folder.Path, options =>
               {
                   options.MinLevel = LogLevel.Warning;
                   options.Filter = _ => ++calls > 0;
               }))
        {
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation("below");
            logger.LogWarning("above");
        }

        Assert.Equal(1, calls);
        Assert.Equal(["above"], Messages());
    }

    [Fact]
    public void AFilterThatThrows_KeepsTheEntry()
    {
        using (var provider = FileLogs.Provider(_folder.Path, options => options.Filter = _ => throw new InvalidOperationException("broken filter")))
            provider.CreateLogger("Cat").LogInformation("still written");

        Assert.Equal(["still written"], Messages());
    }

    [Fact]
    public void TheFilter_IsSetInCode_AlongsideTheConfigurationSection_InARealHost()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _folder.Path });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OmniLogs:File:Directory"] = "logs" });
        builder.Services.AddSingleton<TimeProvider>(new FixedTime(FileLogs.Noon));
        builder.Logging.AddOmniFileLogging(options => options.Filter = candidate => !candidate.Message.Contains("noise", StringComparison.Ordinal));
        using (var host = builder.Build())
        {
            host.Start();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cat");
            logger.LogInformation("noise");
            logger.LogInformation("signal");
        }

        var lines = FileLogs.Lines(Path.Combine(_folder.Path, "logs", "app-20261108.json"));
        Assert.Contains(lines, line => line.Contains("\"@m\":\"signal\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("\"@m\":\"noise\"", StringComparison.Ordinal));
    }
}
