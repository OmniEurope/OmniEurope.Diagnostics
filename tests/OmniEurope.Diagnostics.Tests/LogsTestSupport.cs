// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>A real log folder under the temporary directory, deleted with the test.</summary>
internal sealed class TempLogFolder : IDisposable
{
    public TempLogFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omni-logs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes <paramref name="lines"/>, each ended by a new line, and dates the file.</summary>
    public string Write(string name, IEnumerable<string> lines, DateTime? lastWriteUtc = null)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")), new UTF8Encoding(false));
        if (lastWriteUtc is { } date)
            System.IO.File.SetLastWriteTimeUtc(path, date);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}

/// <summary>Builds lines in Serilog's compact JSON format (CLEF).</summary>
internal static class Clef
{
    public static readonly DateTimeOffset Noon = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One event: <paramref name="template"/> goes to <c>@mt</c>, the properties to the top level.</summary>
    public static string Line(
        DateTimeOffset at, string template, string? level = null, string? category = null, string? exception = null,
        string? rendered = null, params (string Name, object Value)[] properties)
    {
        var json = new Dictionary<string, object?> { ["@t"] = at.ToString("O", CultureInfo.InvariantCulture), ["@mt"] = template };
        if (rendered is not null)
            json["@m"] = rendered;
        if (level is not null)
            json["@l"] = level;
        if (exception is not null)
            json["@x"] = exception;
        if (category is not null)
            json["SourceContext"] = category;
        foreach (var (name, value) in properties)
            json[name] = value;
        return JsonSerializer.Serialize(json);
    }
}

/// <summary>A real ASP.NET Core host serving the logs pages on the test server, in French.</summary>
internal sealed class LogsHost : IAsyncDisposable
{
    private LogsHost(WebApplication app)
    {
        App = app;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<LogsHost> StartAsync(
        string directory, Action<LogsOptions>? configure = null, string pattern = "/logs", TimeProvider? time = null,
        string? contentRoot = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.WebHost.UseTestServer();
        builder.Services.AddOmniLogs(options =>
        {
            options.Directory = directory;
            configure?.Invoke(options);
        });
        if (time is not null)
            builder.Services.AddSingleton(time);
        var app = builder.Build();
        // The test server does not carry the test's culture to the request: the pages are read in French.
        app.Use((context, next) =>
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            return next(context);
        });
        app.MapOmniLogs(pattern);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new LogsHost(app);
    }

    public async Task<string> GetHtmlAsync(string url)
    {
        var response = await Client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        // Decoded: the renderer may encode accented letters and apostrophes, the assertions read the text.
        return System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Reads the rules page and posts <paramref name="fields"/> with its antiforgery cookie and token.</summary>
    public async Task<HttpResponseMessage> PostWithTokenAsync(string url, params (string Name, string Value)[] fields)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await Client.GetAsync("/logs/rules", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        var cookie = page.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))
                .Append(KeyValuePair.Create("__RequestVerificationToken", token))),
        };
        request.Headers.Add("Cookie", cookie);
        return await Client.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> PostWithoutTokenAsync(string url, params (string Name, string Value)[] fields) =>
        await Client.PostAsync(url, new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))),
            TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

/// <summary>A clock the test sets.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
