// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// What a host without ASP.NET Core gets from the Core package: the log reading services registered alone, without
/// pages, Razor or antiforgery, and an assembly that references no part of the ASP.NET Core shared framework.
/// </summary>
public sealed class CorePackageTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task TheLogJournal_IsRegisteredAlone_WithoutPagesOrAntiforgery_AndReadsTheFolder()
    {
        _folder.Write("app-20261108.json", [Clef.Line(Clef.Noon, "Started")]);
        var services = HostServices();

        services.AddOmniLogJournal(options => options.Directory = "ignored");
        services.AddOmniLogJournal(options => options.Directory = _folder.Path);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true
            && !descriptor.ServiceType.Namespace.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(LogsPageServices) || descriptor.ServiceType == typeof(LogsPageState));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ILogJournal));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<ILogJournal>();
        Assert.Equal(["app-20261108.json"], (await journal.ListFilesAsync(TestContext.Current.CancellationToken)).Select(file => file.Name));
        Assert.NotNull(provider.GetRequiredService<ILogRuleStore>());
        Assert.NotNull(provider.GetRequiredService<ILogChanges>());
        Assert.NotNull(provider.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<LogsStrings>>());
    }

    [Fact]
    public void WithoutAHostEnvironment_ARelativeFolder_IsUnderTheApplicationsBaseDirectory()
    {
        var services = HostServices().AddOmniLogJournal(options => options.Directory = "logs");
        using var provider = services.BuildServiceProvider();

        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs")), provider.GetRequiredService<LogDirectory>().FullPath);
    }

    [Fact]
    public void WithoutAHost_UnusableOptions_FailWhenFirstRead()
    {
        using var provider = HostServices().AddOmniLogJournal().BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LogsOptions>>().Value);
    }

    [Fact]
    public void AddOmniLogJournal_RefusesNoServices() =>
        Assert.Throws<ArgumentNullException>(() => LogJournalServiceCollectionExtensions.AddOmniLogJournal(null!));

    [Fact]
    public void AddOmniLogs_AddsThePagesOnTopOfTheJournal()
    {
        var services = new ServiceCollection();

        services.AddOmniLogs(options => options.Directory = _folder.Path);
        services.AddOmniLogs();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ILogJournal));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(LogsPageServices));
        Assert.Contains(services, descriptor => descriptor.ServiceType.FullName == "Microsoft.AspNetCore.Antiforgery.IAntiforgery");
    }

    [Fact]
    public void TheCoreAssembly_ReferencesNoAspNetCoreAssemblyButTheComponents()
    {
        var core = typeof(RequestPerformanceRecorder).Assembly;
        var references = core.GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

        Assert.Equal("OmniEurope.Diagnostics.Core", core.GetName().Name);
        Assert.Contains("Microsoft.AspNetCore.Components", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            && !name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal));
        Assert.DoesNotContain("OmniEurope.Diagnostics", references);
    }

    /// <summary>What every host registers, a .NET MAUI one included: its configuration and logging.</summary>
    private static IServiceCollection HostServices() =>
        new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddLogging();
}
