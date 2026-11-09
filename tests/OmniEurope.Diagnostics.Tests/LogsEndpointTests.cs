// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// End to end, in a real ASP.NET Core host over real CLEF files: the journal, its filters and modes, the export, the
/// download, the rules and the purge, and every protection the pages promise.
/// </summary>
public sealed class LogsEndpointTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
    private readonly TempLogFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void WriteJournal()
    {
        _folder.Write("app-20261107.json", [
            Clef.Line(Clef.Noon.AddDays(-1), "Yesterday {Thing}", properties: [("Thing", "started")]),
        ], Today.AddDays(-1));
        _folder.Write("app-20261108.json", [
            Clef.Line(Clef.Noon, "Failed to determine the https port for redirect.", "Warning", "Microsoft.AspNetCore.HttpsPolicy"),
            Clef.Line(Clef.Noon.AddMinutes(1), "Order {OrderId} failed", "Error", "Shop.Orders", "System.InvalidOperationException: <b>boom</b>",
                properties: [("OrderId", 7), ("RequestId", "0HN8K:4"), ("RequestPath", "/api/orders"), ("RequestMethod", "POST"), ("MachineName", "web-01")]),
            "this line is not JSON",
            Clef.Line(Clef.Noon.AddMinutes(2), "=HYPERLINK(\"http://evil\") clicked", category: "Shop.Ui"),
        ], Today);
    }

    [Fact]
    public async Task TheJournal_IsACompleteUnstyledDocument_NewestFirst_WithItsDetailsAndNotices()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var response = await host.Client.GetAsync("/logs", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var html = WebUtility.HtmlDecode(raw);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("<html lang=\"fr\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" class=", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong aria-current=\"page\">Journal (4)</strong>", html, StringComparison.Ordinal);
        Assert.Contains("Lignes exclues (0)", html, StringComparison.Ordinal);
        Assert.Contains("Règles d'exclusion (0)", html, StringComparison.Ordinal);
        Assert.Contains("Lignes 1 à 4 sur 4, de la plus récente à la plus ancienne.", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("HYPERLINK", StringComparison.Ordinal) < html.IndexOf("Order 7 failed", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Order 7 failed", StringComparison.Ordinal) < html.IndexOf("Yesterday started", StringComparison.Ordinal));
        Assert.Contains("<td><strong>Erreur</strong></td>", html, StringComparison.Ordinal);
        Assert.Contains("<details id=\"entry-2\">", html, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: &lt;b&gt;boom&lt;/b&gt;", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>boom</b>", raw, StringComparison.Ordinal);
        Assert.Contains("<code>POST /api/orders</code>", html, StringComparison.Ordinal);
        Assert.Contains("<code>MachineName</code></td><td>web-01</td>", html, StringComparison.Ordinal);
        Assert.Contains("<code>app-20261108.json</code>, ligne 2", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs?q=0HN8K%3A4\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs/rules?pattern=Order&category=Shop.Orders\"", html, StringComparison.Ordinal);
        Assert.Contains("<code>app-20261108.json</code> n'a pas pu être lu : 1 ligne illisible ignorée (ligne 3).", html, StringComparison.Ordinal);
        Assert.Contains("Précédent | Page 1 sur 1 | Suivant", Regex.Replace(html, @"\s+", " "), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFilters_AndTheFile_NarrowTheJournal_AndARelativeFolderIsUnderTheContentRoot()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(_folder.Path, "logs"));
        File.WriteAllText(Path.Combine(_folder.Path, "logs", "app.json"), string.Concat(
            Clef.Line(Clef.Noon, "info one") + "\n",
            Clef.Line(Clef.Noon.AddHours(1), "warn two", "Warning") + "\n",
            Clef.Line(Clef.Noon.AddHours(2), "error three", "Error", exception: "TimeoutException") + "\n"));
        File.WriteAllText(Path.Combine(_folder.Path, "logs", "other.json"), Clef.Line(Clef.Noon, "elsewhere") + "\n");
        await using var host = await LogsHost.StartAsync("logs", contentRoot: _folder.Path);

        Assert.Contains("Journal (4)", await host.GetHtmlAsync("/logs"), StringComparison.Ordinal);
        Assert.Contains("Journal (2)", await host.GetHtmlAsync("/logs?level=Warning"), StringComparison.Ordinal);
        Assert.Contains("Journal (1)", await host.GetHtmlAsync("/logs?q=timeout"), StringComparison.Ordinal);
        var dated = await host.GetHtmlAsync("/logs?from=2026-11-08T12:30&to=2026-11-08T13:30&file=app.json");
        Assert.Contains("Journal (1)", dated, StringComparison.Ordinal);
        Assert.Contains("warn two", dated, StringComparison.Ordinal);
        Assert.Contains("value=\"2026-11-08T12:30\"", dated, StringComparison.Ordinal);
        Assert.Contains("<option selected>app.json</option>", dated, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs/download?file=app.json\"", dated, StringComparison.Ordinal);
        var onlyOther = await host.GetHtmlAsync("/logs?file=other.json");
        Assert.Contains("elsewhere", onlyOther, StringComparison.Ordinal);
        Assert.DoesNotContain("warn two", onlyOther, StringComparison.Ordinal);
        Assert.Contains("Aucune entrée ne correspond aux filtres.", await host.GetHtmlAsync("/logs?q=nothing-matches"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePages_AreNewestFirst_AndThePageNumberIsKeptInRange()
    {
        _folder.Write("app.json", Enumerable.Range(1, 5).Select(i => Clef.Line(Clef.Noon.AddMinutes(i), "entry " + i)));
        await using var host = await LogsHost.StartAsync(_folder.Path, options => options.PageSize = 2);

        var first = await host.GetHtmlAsync("/logs");
        var second = await host.GetHtmlAsync("/logs?page=2&level=Information");
        var beyond = await host.GetHtmlAsync("/logs?page=9");

        Assert.Equal(["entry 5", "entry 4"], Messages(first));
        Assert.Contains("href=\"/logs?page=2\"", first, StringComparison.Ordinal);
        Assert.Equal(["entry 3", "entry 2"], Messages(second));
        Assert.Contains("<details id=\"entry-3\">", second, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs?level=Information\">Précédent", second, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs?level=Information&page=3\">Suivant", second, StringComparison.Ordinal);
        Assert.Equal(["entry 1"], Messages(beyond));
        Assert.Contains("Lignes 5 à 5 sur 5", beyond, StringComparison.Ordinal);
    }

    private static string[] Messages(string html) =>
        Regex.Matches(html, "<pre>(entry \\d)</pre>").Select(match => match.Groups[1].Value).ToArray();

    [Fact]
    public async Task FollowMode_AddsTheRefresh_AndOffersToStop()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path, options => options.FollowRefreshSeconds = 7);

        var html = await host.GetHtmlAsync("/logs?follow=1&level=Warning");

        Assert.Matches("<meta http-equiv=\"refresh\" content=\"7\"\\s*/?>", html);
        Assert.Contains("Suivi en direct, recharge toutes les 7 s.", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs?level=Warning\">Arrêter le suivi", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/logs?level=Warning&follow=1\">Suivre en direct", await host.GetHtmlAsync("/logs?level=Warning"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCsvExport_IsTheWholeSelection_GuardedAgainstFormulas()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path, options => options.PageSize = 1);

        var response = await host.Client.GetAsync("/logs?format=csv&file=app-20261108.json", Ct);
        var csv = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("logs.csv", response.Content.Headers.ContentDisposition?.FileName);
        var lines = csv.TrimEnd().Split("\r\n");
        Assert.Equal(4, lines.Length); // header + the file's three events, not just the page's one
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\") clicked\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDownload_ServesAListedFile_AsItIsOnDisk()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var response = await host.Client.GetAsync("/logs/download?file=app-20261107.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("app-20261107.json", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(await File.ReadAllBytesAsync(_folder.File("app-20261107.json"), Ct), await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("..\\secret.json", HttpStatusCode.BadRequest)]
    [InlineData("../secret.json", HttpStatusCode.BadRequest)]
    [InlineData("C:\\Windows\\win.ini", HttpStatusCode.BadRequest)]
    [InlineData("/etc/passwd", HttpStatusCode.BadRequest)]
    [InlineData("missing.json", HttpStatusCode.NotFound)]
    [InlineData("omni-log-rules.json", HttpStatusCode.NotFound)]
    [InlineData("notes.txt", HttpStatusCode.NotFound)]
    [InlineData("", HttpStatusCode.BadRequest)]
    public async Task ATraversalOrAnUnlistedFile_IsRefused_ByTheDownloadAndTheJournal(string name, HttpStatusCode expected)
    {
        WriteJournal();
        _folder.Write("omni-log-rules.json", ["{\"rules\":[]}"]);
        _folder.Write("notes.txt", ["secret"]);
        await using var host = await LogsHost.StartAsync(_folder.Path);
        var file = Uri.EscapeDataString(name);

        Assert.Equal(expected, (await host.Client.GetAsync("/logs/download?file=" + file, Ct)).StatusCode);
        if (name.Length > 0)
            Assert.Equal(expected, (await host.Client.GetAsync("/logs?file=" + file, Ct)).StatusCode);
    }

    [Fact]
    public async Task APostWithoutTheAntiforgeryToken_IsRefused_AndChangesNothing()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var rule = await host.PostWithoutTokenAsync("/logs/rules", ("action", "add"), ("pattern", "noise"));
        var purge = await host.PostWithoutTokenAsync("/logs/purge", ("days", "1"), ("confirm", "on"));
        var forged = await host.PostWithoutTokenAsync("/logs/purge", ("days", "1"), ("confirm", "on"), ("__RequestVerificationToken", "forged"));

        Assert.Equal(HttpStatusCode.BadRequest, rule.StatusCode);
        Assert.Contains("Jeton anti-falsification", await rule.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, purge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(File.Exists(_folder.File("omni-log-rules.json")));
        Assert.True(File.Exists(_folder.File("app-20261107.json")));
    }

    [Fact]
    public async Task ARule_PostedWithItsToken_MovesItsLinesToTheExcludedTab_AndBackWhenDisabled()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var prefilled = await host.GetHtmlAsync("/logs/rules?pattern=https+port&category=Microsoft.AspNetCore.HttpsPolicy");
        Assert.Contains("value=\"https port\"", prefilled, StringComparison.Ordinal);
        Assert.Contains("Cette règle écarterait 1 lignes des fichiers actuels.", prefilled, StringComparison.Ordinal);
        Assert.Contains("Aucune règle pour l'instant.", prefilled, StringComparison.Ordinal);

        var added = await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", "https port"), ("category", "Microsoft.AspNetCore.HttpsPolicy"));
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        Assert.Equal("/logs/rules?done=added", added.Headers.Location?.OriginalString);
        var rules = await host.GetHtmlAsync("/logs/rules?done=added");
        Assert.Contains("Règle ajoutée.", rules, StringComparison.Ordinal);
        Assert.Contains("<td>1</td>", rules, StringComparison.Ordinal);
        Assert.Contains("value=\"disable:1\"", rules, StringComparison.Ordinal);

        var journal = await host.GetHtmlAsync("/logs");
        Assert.Contains("Journal (3)", journal, StringComparison.Ordinal);
        Assert.Contains("Règles d'exclusion (1)", journal, StringComparison.Ordinal);
        var excluded = await host.GetHtmlAsync("/logs?tab=excluded");
        Assert.Contains("<strong aria-current=\"page\">Lignes exclues (1)</strong>", excluded, StringComparison.Ordinal);
        Assert.Contains("<details id=\"excluded-1\">", excluded, StringComparison.Ordinal);
        Assert.Contains("<code>https port</code>", excluded, StringComparison.Ordinal);
        Assert.Matches("<input type=\"hidden\" name=\"tab\" value=\"excluded\"\\s*/?>", excluded);

        Assert.Equal(HttpStatusCode.Redirect, (await host.PostWithTokenAsync("/logs/rules", ("action", "disable:1"))).StatusCode);
        Assert.Contains("Journal (4)", await host.GetHtmlAsync("/logs"), StringComparison.Ordinal);
        Assert.Contains("value=\"enable:1\"", await host.GetHtmlAsync("/logs/rules"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await host.PostWithTokenAsync("/logs/rules", ("action", "enable:1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await host.PostWithTokenAsync("/logs/rules", ("action", "delete:1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostWithTokenAsync("/logs/rules", ("action", "delete:1"))).StatusCode);
        Assert.True(File.Exists(_folder.File("app-20261108.json"))); // a rule never deletes a log
    }

    [Theory]
    [InlineData("add", "")]
    [InlineData("add", "   ")]
    [InlineData("rename:1", "x")]
    [InlineData("delete:one", "x")]
    [InlineData("delete", "x")]
    [InlineData("", "x")]
    public async Task AnUnusableRulePost_IsRefused(string action, string pattern)
    {
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var response = await host.PostWithTokenAsync("/logs/rules", ("action", action), ("pattern", pattern));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ATooLongRule_IsRefused_AndAnUnreadableRulesFileIsNeverOverwritten()
    {
        await using var host = await LogsHost.StartAsync(_folder.Path);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", new string('x', 1001)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", "x"), ("category", new string('x', 1001)))).StatusCode);
        _folder.Write("omni-log-rules.json", ["{ broken"]);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", "x"))).StatusCode);
        Assert.Contains("est illisible", await host.GetHtmlAsync("/logs/rules"), StringComparison.Ordinal);
        Assert.Contains("est illisible", await host.GetHtmlAsync("/logs"), StringComparison.Ordinal);
        Assert.Equal("{ broken\n", await File.ReadAllTextAsync(_folder.File("omni-log-rules.json"), Ct));
    }

    [Fact]
    public async Task ARulesFileThatCannotBeWritten_AnswersA500WithItsReason()
    {
        System.IO.Directory.CreateDirectory(_folder.File("omni-log-rules.json")); // a folder in its place: the move fails on every OS
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var response = await host.PostWithTokenAsync("/logs/rules", ("action", "add"), ("pattern", "x"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("n'a pas pu être enregistré", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePurge_DeletesOnlyOldMatchingFiles_AndKeepsTheRulesFile()
    {
        _folder.Write("app-old.json", [Clef.Line(Clef.Noon, "old")], Today.AddDays(-40));
        _folder.Write("app-new.json", [Clef.Line(Clef.Noon, "new")], Today.AddDays(-2));
        _folder.Write("keep.txt", ["not a log"], Today.AddDays(-400));
        _folder.Write("omni-log-rules.json", ["{\"rules\":[]}"], Today.AddDays(-400));
        await using var host = await LogsHost.StartAsync(_folder.Path, time: new FixedTime(Today));

        var form = await host.GetHtmlAsync("/logs/rules");
        Assert.Contains("Je confirme : 1 fichiers (1 Ko) de plus de 30 jours seront supprimés définitivement.", form, StringComparison.Ordinal);
        Assert.Contains("value=\"7\"", await host.GetHtmlAsync("/logs/rules?days=7"), StringComparison.Ordinal);

        var response = await host.PostWithTokenAsync("/logs/purge", ("days", "30"), ("confirm", "on"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logs/rules?purged=1&failed=0", response.Headers.Location?.OriginalString);
        Assert.Equal(["app-new.json", "keep.txt", "omni-log-rules.json"], System.IO.Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Order());
        Assert.Contains("Purge effectuée : 1 fichiers supprimés.", await host.GetHtmlAsync("/logs/rules?purged=1&failed=0"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30", "")]
    [InlineData("0", "on")]
    [InlineData("-1", "on")]
    [InlineData("many", "on")]
    [InlineData("36501", "on")]
    public async Task APurgeWithoutConfirmation_OrWithoutAUsableDayCount_IsRefused(string days, string confirm)
    {
        _folder.Write("app-old.json", [Clef.Line(Clef.Noon, "old")], Today.AddDays(-400));
        await using var host = await LogsHost.StartAsync(_folder.Path);

        var response = await host.PostWithTokenAsync("/logs/purge", ("days", days), ("confirm", confirm));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(File.Exists(_folder.File("app-old.json")));
    }

    [Fact]
    public async Task AnEmptyOrAbsentFolder_SaysWhereItLooked()
    {
        await using var host = await LogsHost.StartAsync(Path.Combine(_folder.Path, "absent"));

        var html = await host.GetHtmlAsync("/logs");

        Assert.Contains("Aucun fichier de log trouvé dans", html, StringComparison.Ordinal);
        Assert.Contains("Aucune entrée ne correspond aux filtres.", html, StringComparison.Ordinal);
        Assert.Contains("Supprimer les fichiers de plus de", await host.GetHtmlAsync("/logs/rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCap_SaysOlderEntriesWereLeftOut()
    {
        _folder.Write("app.json", Enumerable.Range(1, 200).Select(i => Clef.Line(Clef.Noon.AddSeconds(i), "entry " + i + new string('x', 40))));
        await using var host = await LogsHost.StartAsync(_folder.Path, options => options.MaxBytesRead = LogsOptions.MinMaxBytesRead);

        Assert.Contains("Lecture plafonnée à 4 Ko", await host.GetHtmlAsync("/logs"), StringComparison.Ordinal);
        Assert.Contains("la lecture des fichiers est plafonnée", await host.GetHtmlAsync("/logs/rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRequireAuthorization_ProtectsEveryPage_AndTheRequestMetricsLeaveThemOut()
    {
        await using var host = await StartProtectedAsync();

        foreach (var url in new[] { "/logs", "/logs/rules", "/logs/download?file=a.json" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().PostAsync("/logs/purge", null, Ct)).StatusCode);
        var endpoints = host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;
        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IDisableHttpMetricsMetadata>()));
    }

    private async Task<WebApplication> StartProtectedAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DenyAll>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddOmniLogs(options => options.Directory = _folder.Path);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOmniLogs().RequireAuthorization();
        await app.StartAsync(Ct);
        return app;
    }

    [Fact]
    public async Task ACustomAddress_ServesThePages_AndBuildsItsLinksFromIt()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path, pattern: "/admin/logs/");

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/logs", Ct)).StatusCode);
        var html = await host.GetHtmlAsync("/admin/logs");
        Assert.Contains("href=\"/admin/logs/rules\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/admin/logs\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/admin/logs/purge\"", await host.GetHtmlAsync("/admin/logs/rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRootAddress_LinksTheJournalAsTheRoot()
    {
        WriteJournal();
        await using var host = await LogsHost.StartAsync(_folder.Path, pattern: "/");

        var rules = await host.GetHtmlAsync("/rules");

        Assert.Contains("<a href=\"/\">Retour au journal</a>", rules, StringComparison.Ordinal);
        Assert.Contains("action=\"/rules\"", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingWithoutAddOmniLogs_FailsAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() => app.MapOmniLogs());
        Assert.Contains("AddOmniLogs", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => app.MapOmniLogs(" "));
        Assert.Throws<ArgumentNullException>(() => LogsEndpointRouteBuilderExtensions.MapOmniLogs(null!));
    }

    [Fact]
    public async Task AnUnusableOption_StopsTheHostAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOmniLogs();
        var app = builder.Build();
        app.MapOmniLogs();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(Ct));
        Assert.Contains("OmniLogs:Directory", error.Message, StringComparison.Ordinal);
        await app.DisposeAsync();
    }

    [Fact]
    public void AddOmniLogs_IsIdempotent_ReadsItsSection_AndAppliesEveryConfigure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["OmniLogs:Directory"] = "from-config";
        builder.Configuration["OmniLogs:PageSize"] = "20";
        builder.Services.AddOmniLogs().AddOmniLogs(options => options.FollowRefreshSeconds = 9).AddOmniLogs(options => options.PageSize = 30);
        using var provider = builder.Services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<LogsOptions>>().Value;

        Assert.Equal(("from-config", 30, 9), (options.Directory, options.PageSize, options.FollowRefreshSeconds));
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(Logs.LogDirectory));
        Assert.Throws<ArgumentNullException>(() => LogsServiceCollectionExtensions.AddOmniLogs(null!));
    }

    [Fact]
    public async Task TheEnglishCulture_GetsTheEnglishTexts()
    {
        WriteJournal();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOmniLogs(options => options.Directory = _folder.Path);
        await using var app = builder.Build();
        app.UseRequestLocalization("en");
        app.MapOmniLogs();
        await app.StartAsync(Ct);

        var html = await app.GetTestClient().GetStringAsync("/logs", Ct);

        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Application logs</title>", html, StringComparison.Ordinal);
        Assert.Contains("2026-11-08 12:01:00.000", html, StringComparison.Ordinal);
    }

    private sealed class DenyAll(IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    }
}
