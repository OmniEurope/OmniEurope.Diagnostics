// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.FileLogging;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The folder the file logger takes when the host names none.</summary>
public sealed class FileLoggingDirectoryDefaultTests : IDisposable
{
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void InDevelopment_ItIsLogsUnderTheContentRoot()
    {
        Assert.Equal("logs", FileLoggingDirectoryDefault.DefaultFor(new FakeHost("Development", "Shop"), @"C:\Data"));
    }

    [Fact]
    public void ElsewhereItIsTheApplicationsFolderUnderCommonApplicationData()
    {
        var root = Path.Combine(_folder.Path, "common");

        Assert.Equal(Path.Combine(root, "Shop", "logs"), FileLoggingDirectoryDefault.DefaultFor(new FakeHost("Production", "Shop"), root));
        Assert.Equal(Path.Combine(root, "My_App", "logs"), FileLoggingDirectoryDefault.DefaultFor(new FakeHost("Production", " My/App "), root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("...")]
    public void WithoutAnApplicationName_ThereIsNoDefault(string applicationName)
    {
        Assert.Null(FileLoggingDirectoryDefault.DefaultFor(new FakeHost("Production", applicationName), _folder.Path));
    }

    [Fact]
    public void WithoutAUsableHostEnvironment_ThereIsNoDefault()
    {
        Assert.Null(FileLoggingDirectoryDefault.DefaultFor(null));
        Assert.Null(FileLoggingDirectoryDefault.DefaultFor(new FakeHost("Production", "Shop") { Throws = true }));
    }

    [Fact]
    public void ANamedDirectory_IsNeverReplaced()
    {
        var options = new FileLoggingOptions { Directory = "mine" };

        new FileLoggingDirectoryDefault(new FakeHost("Production", "Shop")).PostConfigure(null, options);

        Assert.Equal("mine", options.Directory);
    }

    [Fact]
    public void ADevelopmentHost_WritesUnderItsContentRootWithNoDirectoryGiven()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = _folder.Path,
            EnvironmentName = "Development",
            ApplicationName = "Shop",
        });
        builder.Services.AddSingleton<TimeProvider>(new FixedTime(FileLogs.Noon));
        builder.Logging.AddOmniFileLogging();
        using (var host = builder.Build())
        {
            host.Start();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cat").LogInformation("m");
        }

        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder.Path, "logs"), "app-20261108.json"));
    }

    private sealed class FakeHost(string environmentName, string applicationName) : IHostEnvironment
    {
        public bool Throws { get; init; }

        public string EnvironmentName { get => Throws ? throw new NotImplementedException() : environmentName; set => throw new NotSupportedException(); }

        public string ApplicationName { get => applicationName; set => throw new NotSupportedException(); }

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
