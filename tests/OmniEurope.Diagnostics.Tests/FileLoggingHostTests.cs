// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.FileLogging;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The file logger in a real host: registration, configuration, validation, filters and the reader's view.</summary>
public sealed class FileLoggingHostTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private IHost Start(Dictionary<string, string?> settings, Action<ILoggingBuilder> logging)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _folder.Path });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Services.AddSingleton<TimeProvider>(new FixedTime(FileLogs.Noon));
        logging(builder.Logging);
        var host = builder.Build();
        host.Start();
        return host;
    }

    private static void Log(IHost host, Action<ILoggerFactory> log)
    {
        using (host)
            log(host.Services.GetRequiredService<ILoggerFactory>());
    }

    /// <summary>The lines of <paramref name="category"/>: the host writes its own start and stop entries too.</summary>
    private static string[] Own(string path, string category) =>
        [.. FileLogs.Lines(path).Where(line => line.Contains("\"SourceContext\":\"" + category + "\"", StringComparison.Ordinal))];

    [Fact]
    public void TheSectionIsBound_CodeHasTheLastWord_AndARelativeFolderIsUnderTheContentRoot()
    {
        var host = Start(new() { ["OmniLogs:File:Directory"] = "logs", ["OmniLogs:File:FilePrefix"] = "config", ["OmniLogs:File:Instance"] = "node 1" },
            logging => logging.AddOmniFileLogging(options => options.FilePrefix = "code"));

        Log(host, factory => factory.CreateLogger("Cat").LogInformation("m"));

        Assert.Single(Own(Path.Combine(_folder.Path, "logs", "code-node-1-20261108.json"), "Cat"));
    }

    [Fact]
    public void TheDirectoryOverload_WritesThere()
    {
        Log(Start([], logging => logging.AddOmniFileLogging("other")), factory => factory.CreateLogger("Cat").LogInformation("m"));

        Assert.Single(Own(Path.Combine(_folder.Path, "other", "app-20261108.json"), "Cat"));
    }

    [Fact]
    public void CallingTwice_RegistersOneLogger_AndAppliesEveryDelegate()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOmniFileLogging(options => options.Directory = "x").AddOmniFileLogging(options => options.FilePrefix = "second"));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(FileLoggerProvider));

        Log(Start([], logging => logging.AddOmniFileLogging(options => options.Directory = "x").AddOmniFileLogging(options => options.FilePrefix = "second")),
            factory => factory.CreateLogger("Cat").LogInformation("m"));

        Assert.Single(Own(Path.Combine(_folder.Path, "x", "second-20261108.json"), "Cat"));
    }

    [Fact]
    public void AMissingDirectory_StopsTheHost()
    {
        var error = Assert.Throws<OptionsValidationException>(() => Start([], logging => logging.AddOmniFileLogging()).Dispose());

        Assert.Contains("OmniLogs:File:Directory is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutHostOrClock_TheProviderUsesTheSystemClock_AndAnAbsoluteFolder()
    {
        var before = DateTimeOffset.UtcNow;
        using (var provider = new FileLoggerProvider(Options.Create(new FileLoggingOptions { Directory = _folder.Path })))
            provider.CreateLogger("Cat").LogInformation("m");

        var name = Path.GetFileName(Assert.Single(Directory.EnumerateFiles(_folder.Path)));
        var after = DateTimeOffset.UtcNow;
        Assert.Contains(name, new[] { before, after }.Select(at => "app-" + at.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".json"));
    }

    [Fact]
    public void TheArguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => FileLoggingBuilderExtensions.AddOmniFileLogging(null!));
        var logging = new ServiceCollection().AddLogging();
        Assert.Throws<ArgumentException>(() => new LoggingBuilder(logging).AddOmniFileLogging(" "));
    }

    [Fact]
    public void TheLoggingFilters_ApplyUnderTheOmniFileAlias()
    {
        var host = Start(new()
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:OmniFile:LogLevel:Default"] = "Warning",
            ["Logging:OmniFile:LogLevel:Noisy"] = "Error",
            ["OmniLogs:File:Directory"] = ".",
        }, logging => logging.AddOmniFileLogging());

        Log(host, factory =>
        {
            factory.CreateLogger("Cat").LogInformation("info");
            factory.CreateLogger("Cat").LogWarning("warning");
            factory.CreateLogger("Noisy").LogWarning("noisy warning");
            factory.CreateLogger("Noisy").LogError("noisy error");
        });

        Assert.Equal(["warning", "noisy error"], FileLogs.Entries(_folder.File("app-20261108.json")).Select(entry => entry.Text("@m")));
    }

    [Fact]
    public void AWrittenLine_IsReadBackByTheLogsPagesParser()
    {
        var error = new InvalidOperationException("boom");
        var host = Start(new() { ["OmniLogs:File:Directory"] = "." }, logging => logging.AddOmniFileLogging());

        Log(host, factory =>
        {
            var logger = factory.CreateLogger("Shop.Orders");
            using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "0HN:7", ["RequestPath"] = "/api/orders", ["RequestMethod"] = "POST" }))
                logger.LogError(error, "Failed {Count} orders", 3);
        });

        var line = Assert.Single(Own(_folder.File("app-20261108.json"), "Shop.Orders"));
        var entry = LogLineParser.Parse(Encoding.UTF8.GetBytes(line));
        Assert.NotNull(entry);
        Assert.Equal((FileLogs.Noon, LogSeverity.Error, "Failed 3 orders", "Failed {Count} orders"), (entry.Timestamp, entry.Level, entry.Message, entry.Template));
        Assert.Equal(("Shop.Orders", error.ToString(), "0HN:7", "POST /api/orders"), (entry.Category, entry.Exception, entry.CorrelationId, entry.Request));
        Assert.Contains(KeyValuePair.Create("Count", "3"), entry.Properties);
    }

    [Fact]
    public async Task TheLogsPages_WithoutAFolder_ReadTheLoggersFolderAndOnlyItsFiles()
    {
        File.WriteAllText(Path.Combine(_folder.Path, "other.json"), "{\"@t\":\"2026-11-08T12:00:00Z\",\"@m\":\"foreign\"}\n");
        var host = Start(new() { ["OmniLogs:File:Directory"] = "." }, logging =>
        {
            logging.AddOmniFileLogging();
            logging.Services.AddOmniLogs();
        });
        var options = host.Services.GetRequiredService<IOptions<LogsOptions>>().Value;
        Assert.Equal((".", "app*.json"), (options.Directory, options.FilePattern));

        Log(host, factory => factory.CreateLogger("Shop.Orders").LogError("payment refused"));

        using var reader = StartReader();
        using var scope = reader.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<ILogJournal>();
        var found = await journal.SearchAsync(new LogSearch { Text = "payment refused" }, TestContext.Current.CancellationToken);
        Assert.Equal(LogSeverity.Error, Assert.Single(found.Entries).Entry.Level);
        Assert.DoesNotContain(await journal.ListFilesAsync(TestContext.Current.CancellationToken), file => file.Name == "other.json");
    }

    [Fact]
    public void AFolderTheHostSets_KeepsItsOwnFolderAndPattern()
    {
        var host = Start(new() { ["OmniLogs:File:Directory"] = "written", ["OmniLogs:Directory"] = "read", ["OmniLogs:FilePattern"] = "*.json" },
            logging =>
            {
                logging.AddOmniFileLogging();
                logging.Services.AddOmniLogs();
            });
        using (host)
        {
            var options = host.Services.GetRequiredService<IOptions<LogsOptions>>().Value;
            Assert.Equal(("read", LogsOptions.DefaultFilePattern), (options.Directory, options.FilePattern));
        }
    }

    // A second host over the same folder: the first one is disposed, so its file is complete.
    private IHost StartReader() => Start(new() { ["OmniLogs:File:Directory"] = "." }, logging =>
    {
        logging.AddOmniFileLogging();
        logging.Services.AddOmniLogs();
    });

    private sealed class LoggingBuilder(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

/// <summary>The checks the host runs on <see cref="FileLoggingOptions"/> when it starts.</summary>
public sealed class FileLoggingOptionsTests
{
    private static readonly FileLoggingOptionsValidator Validator = new();

    [Fact]
    public void TheDefaults_WithAFolder_AreValid()
    {
        var options = new FileLoggingOptions { Directory = "logs" };

        Assert.True(Validator.Validate(null, options).Succeeded);
        Assert.Equal(("app", null, LogLevel.Information, 30, 100L * 1024 * 1024, 8192),
            (options.FilePrefix, options.Instance, options.MinLevel, options.RetentionDays, options.MaxFileBytes, options.QueueCapacity));
        Assert.True(Validator.Validate(null, new FileLoggingOptions { Directory = "logs", MinLevel = LogLevel.None, RetentionDays = 0 }).Succeeded);
    }

    public static TheoryData<string, string> Invalid() => new()
    {
        { nameof(FileLoggingOptions.Directory), "OmniLogs:File:Directory is required" },
        { "Directory=whitespace", "OmniLogs:File:Directory is required" },
        { "FilePrefix=", "OmniLogs:File:FilePrefix ''" },
        { "FilePrefix=.app", "OmniLogs:File:FilePrefix '.app'" },
        { "FilePrefix=a/b", "OmniLogs:File:FilePrefix 'a/b'" },
        { "FilePrefix=long", "OmniLogs:File:FilePrefix '" },
        { "MinLevel=42", "OmniLogs:File:MinLevel 42" },
        { "RetentionDays=-1", "OmniLogs:File:RetentionDays -1" },
        { "RetentionDays=3651", "OmniLogs:File:RetentionDays 3651" },
        { "MaxFileBytes=4095", "OmniLogs:File:MaxFileBytes 4095" },
        { "MaxFileBytes=huge", "OmniLogs:File:MaxFileBytes 1073741825" },
        { "QueueCapacity=15", "OmniLogs:File:QueueCapacity 15" },
        { "QueueCapacity=huge", "OmniLogs:File:QueueCapacity 1048577" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void AnUnusableValue_IsRefused_WithItsName(string change, string expected)
    {
        var options = new FileLoggingOptions { Directory = "logs" };
        switch (change)
        {
            case nameof(FileLoggingOptions.Directory): options.Directory = ""; break;
            case "Directory=whitespace": options.Directory = "  "; break;
            case "FilePrefix=": options.FilePrefix = ""; break;
            case "FilePrefix=.app": options.FilePrefix = ".app"; break;
            case "FilePrefix=a/b": options.FilePrefix = "a/b"; break;
            case "FilePrefix=long": options.FilePrefix = new string('a', FileLoggingOptions.MaxNameLength + 1); break;
            case "MinLevel=42": options.MinLevel = (LogLevel)42; break;
            case "RetentionDays=-1": options.RetentionDays = -1; break;
            case "RetentionDays=3651": options.RetentionDays = FileLoggingOptions.MaxRetentionDays + 1; break;
            case "MaxFileBytes=4095": options.MaxFileBytes = FileLoggingOptions.MinMaxFileBytes - 1; break;
            case "MaxFileBytes=huge": options.MaxFileBytes = FileLoggingOptions.MaxMaxFileBytes + 1; break;
            case "QueueCapacity=15": options.QueueCapacity = FileLoggingOptions.MinQueueCapacity - 1; break;
            default: options.QueueCapacity = FileLoggingOptions.MaxQueueCapacity + 1; break;
        }

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expected, Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RefusesNoOptions() =>
        Assert.Throws<ArgumentNullException>(() => Validator.Validate(null, null!));
}
