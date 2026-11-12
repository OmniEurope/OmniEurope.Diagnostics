// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.FileLogging;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// The folder a relative log folder is resolved against: the host's content root, or the application's base
/// directory when the host has no environment or one that implements no content root, as the environment a .NET
/// MAUI application registers.
/// </summary>
public sealed class HostContentRootTests
{
    [Fact]
    public void TheContentRoot_IsTheHostOwn()
    {
        Assert.Equal("/srv/app", HostContentRoot.Of(new Environment(() => "/srv/app")));
    }

    [Fact]
    public void WithoutAnEnvironment_OrWithoutAContentRoot_OrWithAnUnimplementedOne_ItIsTheBaseDirectory()
    {
        Assert.Equal(AppContext.BaseDirectory, HostContentRoot.Of(null));
        Assert.Equal(AppContext.BaseDirectory, HostContentRoot.Of(new Environment(() => null!)));
        Assert.Equal(AppContext.BaseDirectory, HostContentRoot.Of(new Environment(() => throw new NotImplementedException())));
    }

    [Fact]
    public void AnotherFailure_IsNotHidden()
    {
        Assert.Throws<InvalidOperationException>(() => HostContentRoot.Of(new Environment(() => throw new InvalidOperationException())));
    }

    [Fact]
    public void TheLogFolderAndTheFileLogger_AcceptAnEnvironmentWithoutAContentRoot()
    {
        var unimplemented = new Environment(() => throw new NotImplementedException());
        var folder = Path.Combine(AppContext.BaseDirectory, "relative-logs");

        var directory = new LogDirectory(Options.Create(new LogsOptions { Directory = "relative-logs" }), unimplemented);
        Assert.Equal(folder, directory.FullPath);

        using var provider = new FileLoggerProvider(Options.Create(new FileLoggingOptions { Directory = "relative-logs" }), unimplemented);
        Assert.NotNull(provider);
    }

    private sealed class Environment(Func<string> contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get => contentRoot(); set => throw new NotSupportedException(); }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
