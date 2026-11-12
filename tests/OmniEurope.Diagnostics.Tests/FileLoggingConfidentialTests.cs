// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniEurope.Diagnostics.FileLogging;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>The confidential mode of the file logger: the template only, no values, the exception's outline only.</summary>
public sealed class FileLoggingConfidentialTests : IDisposable
{
    private const string SecretPath = @"C:\Users\x\secret";
    private const string ExceptionMessage = @"The process cannot access the file 'C:\Users\x\secret\data.db'.";

    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string[] WriteFailedRead(bool confidential)
    {
        using (var provider = FileLogs.Provider(_folder.Path, options => options.Confidential = confidential))
        {
            provider.SetScopeProvider(new LoggerExternalScopeProvider());
            var logger = provider.CreateLogger("Shop.Files");
            using (logger.BeginScope(new Dictionary<string, object?> { ["User"] = "alice" }))
            using (logger.BeginScope("scope text alice"))
                logger.LogWarning(Thrown(), "Read {Path} failed", SecretPath);
        }
        return FileLogs.Lines(_folder.File("app-20261108.json"));
    }

    private static IOException Thrown()
    {
        try
        {
            throw new IOException(ExceptionMessage, new UnauthorizedAccessException("denied for alice"));
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    [Fact]
    public void Confidential_WritesTheTemplateAndTheExceptionType_NeitherThePathNorTheExceptionMessage()
    {
        var line = Assert.Single(WriteFailedRead(confidential: true));

        Assert.DoesNotContain(SecretPath, line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPath.Replace(@"\", @"\\", StringComparison.Ordinal), line, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot access", line, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", line, StringComparison.Ordinal);
        Assert.Contains("\"@mt\":\"Read {Path} failed\"", line, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", line, StringComparison.Ordinal);
        Assert.Contains("\"SourceContext\":\"Shop.Files\"", line, StringComparison.Ordinal);
        var entry = FileLogs.Entries(_folder.File("app-20261108.json"))[0];
        Assert.Null(entry.Text("@m"));
        Assert.Null(entry.Text("Path"));
        Assert.Null(entry.Text("User"));
        Assert.Null(entry.Text("Scope"));
        Assert.Equal(
            $"System.IO.IOException (HResult 0x80131620) in {typeof(FileLoggingConfidentialTests).FullName}.{nameof(Thrown)}"
            + "\n ---> System.UnauthorizedAccessException (HResult 0x80070005)",
            entry.Text("@x"));
    }

    [Fact]
    public void Confidential_TheLogViewReadsTheTemplate_AsTheMessage()
    {
        var line = Assert.Single(WriteFailedRead(confidential: true));

        var entry = LogLineParser.Parse(Encoding.UTF8.GetBytes(line))!;

        Assert.Equal("Read {Path} failed", entry.Message);
        Assert.Equal(LogSeverity.Warning, entry.Level);
        Assert.Equal("Shop.Files", entry.Category);
    }

    [Fact]
    public void ByDefault_TheEntryIsWrittenWhole()
    {
        var line = Assert.Single(WriteFailedRead(confidential: false));

        var entry = FileLogs.Entries(_folder.File("app-20261108.json"))[0];
        Assert.Equal("Read " + SecretPath + " failed", entry.Text("@m"));
        Assert.Equal(SecretPath, entry.Text("Path"));
        Assert.Equal("alice", entry.Text("User"));
        Assert.Contains(ExceptionMessage, entry.Text("@x"), StringComparison.Ordinal);
        Assert.Contains("denied for alice", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_OfAnExceptionNeverThrown_HasNoMethod()
    {
        Assert.Equal("System.InvalidOperationException (HResult 0x80131509)", ClefFormatter.Outline(new InvalidOperationException("secret")));
    }

    [Fact]
    public void Confidential_IsReadFromTheConfigurationSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _folder.Path });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OmniLogs:File:Directory"] = "logs",
            ["OmniLogs:File:Confidential"] = "true",
        });
        builder.Services.AddSingleton<TimeProvider>(new FixedTime(FileLogs.Noon));
        builder.Logging.AddOmniFileLogging();
        using (var host = builder.Build())
        {
            host.Start();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cat").LogInformation("Opened {Path}", SecretPath);
        }

        var line = Assert.Single(FileLogs.Lines(Path.Combine(_folder.Path, "logs", "app-20261108.json")), text => text.Contains("Opened", StringComparison.Ordinal));
        Assert.Contains("\"@mt\":\"Opened {Path}\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }
}
