// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Hosting;
using OmniEurope.Diagnostics.Logs;
using OmniEurope.Diagnostics.Maui;

namespace OmniEurope.Diagnostics.Maui.Tests;

/// <summary>
/// The registrations of <c>UseOmniDiagnostics</c> on a real <see cref="MauiAppBuilder"/>, started without a window:
/// what resolves, where the logs go, what the initializer starts, what a host can replace. The save dialog itself
/// needs a window and a user, so it is not covered here: the sample application exercises it.
/// </summary>
public sealed class UseOmniDiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "omni-maui-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<ServiceProvider> _apps = [];

    public void Dispose()
    {
        foreach (var app in _apps) app.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>
    /// Builds the container the way <c>MauiAppBuilder.Build</c> does, then runs this package's initializer.
    /// <c>MauiAppBuilder.Build</c> itself cannot run in a test process (MAUI's own core initializer fails with a
    /// <see cref="NullReferenceException"/> without the Windows UI application), so MAUI's initializers are left
    /// out; the registrations, the options and this package's initializer are the real ones.
    /// </summary>
    private ServiceProvider Build(Action<OmniMauiDiagnosticsOptions>? configure = null, Action<MauiAppBuilder>? before = null)
    {
        var builder = MauiApp.CreateBuilder();
        before?.Invoke(builder);
        builder.UseOmniDiagnostics(configure);
        return Start(builder);
    }

    private ServiceProvider Start(MauiAppBuilder builder)
    {
        var app = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _apps.Add(app);
        foreach (var initializer in app.GetServices<IMauiInitializeService>().OfType<OmniDiagnosticsInitializer>())
            initializer.Initialize(app);
        return app;
    }

    private static string DefaultFolder => Path.Combine(FileSystem.AppDataDirectory, "logs");

    [Fact]
    public void EveryService_ResolvesFromTheMauiContainer()
    {
        var app = Build(options => options.LogDirectory = _folder);

        using var scope = app.CreateScope();
        var services = scope.ServiceProvider;
        Assert.NotNull(services.GetRequiredService<RequestPerformanceRecorder>());
        Assert.NotNull(services.GetRequiredService<ILogJournal>());
        Assert.NotNull(services.GetRequiredService<ILogRuleStore>());
        Assert.NotNull(services.GetRequiredService<ILogChanges>());
        Assert.IsType<FileSavePickerLogFileExport>(services.GetRequiredService<ILogFileExport>());
        // The package's own texts are internal; resolving a localizer proves the factory and its ILoggerFactory exist.
        Assert.NotNull(services.GetRequiredService<IStringLocalizer<UseOmniDiagnosticsTests>>());
        Assert.Contains(services.GetServices<ILoggerProvider>(), provider => provider.GetType().Name == "FileLoggerProvider");
    }

    [Fact]
    public void WithoutAnyOption_TheLogsLiveInTheAppDataFolder_AndThePatternReadsPlainTextToo()
    {
        var app = Build();

        Assert.Equal(DefaultFolder, app.GetRequiredService<IOptions<FileLoggingOptions>>().Value.Directory);
        var logs = app.GetRequiredService<IOptions<LogsOptions>>().Value;
        Assert.Equal(DefaultFolder, logs.Directory);
        Assert.Equal("*.json;*.log", logs.FilePattern);
        Assert.Equal(OmniMauiDiagnosticsOptions.DefaultLogFilePattern, logs.FilePattern);
    }

    [Fact]
    public void TheOptions_OverrideTheFolderAndThePattern_OfBothServices()
    {
        var app = Build(options =>
        {
            options.LogDirectory = _folder;
            options.LogFilePattern = "*.txt";
        });

        Assert.Equal(_folder, app.GetRequiredService<IOptions<FileLoggingOptions>>().Value.Directory);
        var logs = app.GetRequiredService<IOptions<LogsOptions>>().Value;
        Assert.Equal(_folder, logs.Directory);
        Assert.Equal("*.txt", logs.FilePattern);
    }

    [Fact]
    public void TheConfiguration_FillsWhatTheOptionsLeaveOut_AndTheOptionsWinOverIt()
    {
        var settings = new Dictionary<string, string?>
        {
            ["OmniLogs:File:Directory"] = Path.Combine(_folder, "from-configuration"),
            ["OmniLogs:FilePattern"] = "*.ndjson",
        };

        var configured = Build(before: builder => builder.Configuration.AddInMemoryCollection(settings));
        Assert.Equal(Path.Combine(_folder, "from-configuration"), configured.GetRequiredService<IOptions<FileLoggingOptions>>().Value.Directory);
        Assert.Equal(Path.Combine(_folder, "from-configuration"), configured.GetRequiredService<IOptions<LogsOptions>>().Value.Directory);
        Assert.Equal("*.ndjson", configured.GetRequiredService<IOptions<LogsOptions>>().Value.FilePattern);

        var overridden = Build(
            options => (options.LogDirectory, options.LogFilePattern) = (_folder, "*.json"),
            builder => builder.Configuration.AddInMemoryCollection(settings));
        Assert.Equal(_folder, overridden.GetRequiredService<IOptions<FileLoggingOptions>>().Value.Directory);
        Assert.Equal("*.json", overridden.GetRequiredService<IOptions<LogsOptions>>().Value.FilePattern);
    }

    [Fact]
    public void TheDelegates_ReachTheUnderlyingOptions_AndHaveTheLastWord()
    {
        var app = Build(options =>
        {
            options.LogDirectory = _folder;
            options.ConfigureFileLogging = file => (file.FilePrefix, file.RetentionDays) = ("sample", 5);
            options.ConfigureLogs = logs => (logs.PageSize, logs.FilePattern) = (25, "*.log");
            options.ConfigurePerformance = performance => performance.SlowestCount = 7;
        });

        var file = app.GetRequiredService<IOptions<FileLoggingOptions>>().Value;
        Assert.Equal(("sample", 5), (file.FilePrefix, file.RetentionDays));
        var logs = app.GetRequiredService<IOptions<LogsOptions>>().Value;
        Assert.Equal((25, "*.log"), (logs.PageSize, logs.FilePattern));
        Assert.Equal(7, app.GetRequiredService<IOptions<PerformanceOptions>>().Value.SlowestCount);
    }

    [Fact]
    public void TheInitializer_StartsTheRecorder_SoAHostInstrumentIsHeardWithoutAnyCallToStart()
    {
        var meterName = "OmniEurope.Diagnostics.Maui.Tests." + Guid.NewGuid().ToString("N");
        var app = Build(options =>
        {
            options.LogDirectory = _folder;
            options.ConfigurePerformance = performance =>
                performance.Instruments.Add(new PerformanceInstrument { MeterName = meterName, InstrumentName = "app.duration" });
        });
        var recorder = app.GetRequiredService<RequestPerformanceRecorder>();

        using var meter = new Meter(meterName);
        meter.CreateHistogram<double>("app.duration", unit: "ms").Record(12.5, new KeyValuePair<string, object?>("operation", "Load screen"));
        recorder.RecordOperation("Read file", TimeSpan.FromMilliseconds(3));

        var routes = recorder.Summarize().Routes;
        Assert.Contains(routes, route => route.IsOperation && route.Route == "Load screen" && route.Count == 1 && route.MaxMs == 12.5);
        Assert.Contains(routes, route => route.IsOperation && route.Route == "Read file");
    }

    [Fact]
    public void TheRecorder_IsDisposedWithTheContainer()
    {
        var app = Build(options => options.LogDirectory = _folder);
        var recorder = app.GetRequiredService<RequestPerformanceRecorder>();

        app.Dispose();

        Assert.Throws<ObjectDisposedException>(recorder.Start);
    }

    [Fact]
    public void AnInvalidOption_StopsTheApplicationWhenItStarts()
    {
        // The startup validator gathers every failure in an AggregateException.
        var failure = Assert.Throws<AggregateException>(() =>
            Build(options => options.ConfigureFileLogging = file => (file.Directory, file.FilePrefix) = (_folder, "no spaces allowed")));

        Assert.All(failure.InnerExceptions, inner =>
        {
            var validation = Assert.IsType<OptionsValidationException>(inner);
            Assert.Contains("FilePrefix", validation.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TheFileLogger_WritesToTheFolder_AndTheJournalReadsJsonAndPlainTextFiles()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "crash.log"), "2026-11-12 09:00:00 Unhandled exception in the host", TestContext.Current.CancellationToken);
        var app = Build(options => options.LogDirectory = _folder);

        app.GetRequiredService<ILoggerFactory>().CreateLogger("Tests").LogError("Disk is {State}", "full");
        app.Dispose(); // flushes the file logger's queue

        var json = Assert.Single(Directory.GetFiles(_folder, "*.json"));
        Assert.Contains("Disk is full", await File.ReadAllTextAsync(json, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var reader = Build(options => options.LogDirectory = _folder);
        using var scope = reader.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<ILogJournal>();
        var names = (await journal.ListFilesAsync(TestContext.Current.CancellationToken)).Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Path.GetFileName(json), "crash.log"], names);
        var result = await journal.SearchAsync(new LogSearch { File = "crash.log" }, TestContext.Current.CancellationToken);
        Assert.Contains(result.Entries, row => row.Entry.Message.Contains("Unhandled exception in the host", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExport_IsReplaceableByTheHost_BeforeOrAfterTheCall()
    {
        var before = Build(
            options => options.LogDirectory = _folder,
            builder => builder.Services.AddSingleton<ILogFileExport, RecordingExport>());
        Assert.IsType<RecordingExport>(before.GetRequiredService<ILogFileExport>());

        var builder = MauiApp.CreateBuilder();
        builder.UseOmniDiagnostics(options => options.LogDirectory = _folder);
        builder.Services.AddSingleton<ILogFileExport, RecordingExport>();
        var after = Start(builder);
        Assert.IsType<RecordingExport>(after.GetRequiredService<ILogFileExport>());
    }

    [Fact]
    public void CallingItTwice_RegistersOneInitializerAndOneExport()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseOmniDiagnostics(options => options.LogDirectory = _folder).UseOmniDiagnostics();

        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ILogFileExport));
        Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IMauiInitializeService) && descriptor.ImplementationType == typeof(OmniDiagnosticsInitializer));
        Assert.Throws<ArgumentNullException>(() => ((MauiAppBuilder)null!).UseOmniDiagnostics());
    }

    [Theory]
    [InlineData("logs-20261112.json", ".json")]
    [InlineData("export.CSV", ".CSV")]
    [InlineData("crash.log", ".log")]
    [InlineData("noextension", ".txt")]
    [InlineData("trailingdot.", ".txt")]
    public void TheSaveDialog_OffersTheFileOwnExtension_OrTxt(string fileName, string expected)
    {
        Assert.Equal(expected, FileSavePickerLogFileExport.ExtensionOf(fileName));
    }

    private sealed class RecordingExport : ILogFileExport
    {
        public Task ExportAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
