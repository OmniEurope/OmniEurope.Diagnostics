// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.Components;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// The requests of the logs pages. Every post checks the antiforgery token itself
/// (<see cref="IAntiforgery.IsRequestValidAsync"/>) before reading a field, so the pages are protected whether or
/// not the host runs <c>UseAntiforgery()</c>, and every post answers with a redirect to a page (post, redirect, get).
/// </summary>
/// <param name="pattern">The address the pages were mapped on.</param>
internal sealed class LogsEndpointHandlers(string pattern)
{
    /// <summary>The purge form's days when the address names none.</summary>
    public const int DefaultPurgeDays = 30;

    /// <summary>The most days a purge accepts: a hundred years.</summary>
    public const int MaxPurgeDays = 36500;

    private readonly string _pattern = pattern.Trim().Trim('/');

    /// <summary>GET <c>{pattern}</c>: the journal or excluded tab, or the CSV export of its selection.</summary>
    public IResult Journal(HttpContext context)
    {
        var services = Services.Of(context);
        var query = LogQuery.Parse(context.Request.Query);
        var files = services.Directory.List();
        var (lookup, file) = LogDirectory.Find(query.File, files);
        if (Refusal(lookup) is { } refusal)
            return refusal;
        var read = LogFileReader.Read(file is null ? files : [file], services.Options.MaxBytesRead);
        var rules = services.Rules.Load();
        var (journal, excluded) = LogSelection.Split(read.Entries, rules.Rules, query);
        var rows = query.Excluded ? excluded : journal;
        if (query.Csv)
            return TypedResults.File(LogsCsv.Write(rows), "text/csv; charset=utf-8", "logs.csv");

        var pageSize = services.Options.PageSize;
        var pageCount = Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
        var page = Math.Min(query.Page, pageCount);
        services.State.Journal = new LogsJournalModel
        {
            Links = Links(context),
            Query = query,
            Files = files.Select(listed => listed.Name).ToList(),
            Source = services.Directory.Source,
            DirectoryLabel = services.Directory.DirectoryLabel,
            RulesLabel = services.Directory.RulesLabel,
            ReadAt = services.Time.GetUtcNow(),
            JournalCount = journal.Count,
            ExcludedCount = excluded.Count,
            RuleCount = rules.Rules.Count,
            Rows = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageCount = pageCount,
            Offset = (page - 1) * pageSize,
            Issues = read.Issues,
            Truncated = read.Truncated,
            MaxBytesRead = services.Options.MaxBytesRead,
            RulesError = rules.Error,
            FollowSeconds = services.Options.FollowRefreshSeconds,
        };
        return new RazorComponentResult<LogsPage>();
    }

    /// <summary>GET <c>{pattern}/download?file=</c>: a listed log file, as it is on disk.</summary>
    public IResult Download(HttpContext context)
    {
        var services = Services.Of(context);
        var (lookup, file) = LogDirectory.Find(context.Request.Query["file"].ToString(), services.Directory.List());
        if (lookup == LogFileLookup.None)
            return TypedResults.BadRequest();
        if (Refusal(lookup) is { } refusal)
            return refusal;
        FileStream source;
        try
        {
            source = LogFileReader.OpenShared(file!.FullName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted by the sink's retention between the listing and the opening.
            return TypedResults.NotFound();
        }
        // Sent as it was when opened, without a length announced up front: the sink may append to it meanwhile.
        return TypedResults.Stream(async body =>
        {
            await using (source)
                await LogFileReader.CopySnapshotAsync(source, body, context.RequestAborted);
        }, "application/octet-stream", file.Name);
    }

    /// <summary>GET <c>{pattern}/rules</c>: the rules, the new-rule form (prefilled from the address) and the purge.</summary>
    public IResult RulesPage(HttpContext context)
    {
        var services = Services.Of(context);
        var request = context.Request.Query;
        var files = services.Directory.List();
        var read = LogFileReader.Read(files, services.Options.MaxBytesRead);
        var rules = services.Rules.Load();
        var pattern = Text(request["pattern"]);
        var days = ParseDays(request["days"]) ?? DefaultPurgeDays;
        var old = services.Directory.OlderThan(days, services.Time.GetUtcNow());
        var tokens = services.Antiforgery.GetAndStoreTokens(context);
        services.State.Rules = new LogRulesModel
        {
            Links = Links(context),
            RulesLabel = services.Directory.RulesLabel,
            TokenField = tokens.FormFieldName,
            Token = tokens.RequestToken,
            Pattern = pattern,
            Category = Text(request["category"]),
            PrefillCount = pattern is null ? null : Count(read, new LogRule(0, pattern, Text(request["category"]), Enabled: true)),
            Rules = rules.Rules.Select(rule => new LogRuleCount(rule, rule.Enabled ? Count(read, rule) : 0)).ToList(),
            RulesError = rules.Error,
            PurgeDays = days,
            PurgeCount = old.Count,
            PurgeBytes = old.Sum(file => file.Length),
            Done = Text(request["done"]),
            Purged = ParsePurge(request),
            Truncated = read.Truncated,
        };
        return new RazorComponentResult<LogRulesPage>();
    }

    /// <summary>POST <c>{pattern}/rules</c>: <c>action=add</c> (with <c>pattern</c>, <c>category</c>),
    /// <c>enable:N</c>, <c>disable:N</c> or <c>delete:N</c>.</summary>
    public async Task<IResult> PostRules(HttpContext context)
    {
        var services = Services.Of(context);
        if (!await services.Antiforgery.IsRequestValidAsync(context))
            return Refused(services, "RefusedToken");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var action = form["action"].ToString();
        if (action == "add")
            return AddRule(context, services, form);
        var parts = action.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return Refused(services, "RefusedAction");
        var (update, done) = parts[0] switch
        {
            "enable" => (services.Rules.SetEnabled(id, enabled: true), "enabled"),
            "disable" => (services.Rules.SetEnabled(id, enabled: false), "disabled"),
            "delete" => (services.Rules.Delete(id), "deleted"),
            _ => ((LogRuleUpdate?)null, ""),
        };
        return update is { } result ? Saved(context, result, done) : Refused(services, "RefusedAction");
    }

    /// <summary>POST <c>{pattern}/purge</c>: <c>days</c> (at least 1) and the <c>confirm</c> box, both required.</summary>
    public async Task<IResult> Purge(HttpContext context)
    {
        var services = Services.Of(context);
        if (!await services.Antiforgery.IsRequestValidAsync(context))
            return Refused(services, "RefusedToken");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (string.IsNullOrEmpty(form["confirm"].ToString()))
            return Refused(services, "RefusedConfirm");
        if (ParseDays(form["days"]) is not { } days)
            return Refused(services, "RefusedDays");
        var purged = services.Directory.Purge(days, services.Time.GetUtcNow());
        return TypedResults.Redirect(Links(context).Rules + QueryString.Create([
            KeyValuePair.Create("purged", (string?)purged.Deleted.ToString(CultureInfo.InvariantCulture)),
            KeyValuePair.Create("failed", (string?)purged.Failed.ToString(CultureInfo.InvariantCulture)),
        ]).ToUriComponent());
    }

    private IResult AddRule(HttpContext context, Services services, IFormCollection form)
    {
        var pattern = Text(form["pattern"]);
        var category = Text(form["category"]);
        if (pattern is null || pattern.Length > LogRule.MaxLength || category?.Length > LogRule.MaxLength)
            return Refused(services, "RefusedPattern");
        return Saved(context, services.Rules.Add(pattern, category), "added");
    }

    private IResult Saved(HttpContext context, LogRuleUpdate update, string done) => update switch
    {
        LogRuleUpdate.Saved => TypedResults.Redirect(Links(context).Rules + QueryString.Create("done", done).ToUriComponent()),
        LogRuleUpdate.NotFound => TypedResults.NotFound(),
        LogRuleUpdate.WriteFailed => TypedResults.Text(Services.Of(context).Strings["RulesWriteFailed"], "text/plain; charset=utf-8",
            statusCode: StatusCodes.Status500InternalServerError),
        _ => TypedResults.Conflict(),
    };

    private static IResult Refused(Services services, string reason) =>
        TypedResults.Text(services.Strings[reason], "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);

    private static IResult? Refusal(LogFileLookup lookup) => lookup switch
    {
        LogFileLookup.Invalid => TypedResults.BadRequest(),
        LogFileLookup.Missing => TypedResults.NotFound(),
        _ => null,
    };

    private LogsLinks Links(HttpContext context) =>
        new(context.Request.PathBase.Value + (_pattern.Length == 0 ? "" : "/" + _pattern));

    private static int Count(LogReadResult read, LogRule rule) => read.Entries.Count(rule.Matches);

    private static string? Text(Microsoft.Extensions.Primitives.StringValues value)
    {
        var text = value.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static int? ParseDays(Microsoft.Extensions.Primitives.StringValues value) =>
        int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= MaxPurgeDays ? days : null;

    private static LogPurgeResult? ParsePurge(IQueryCollection query) =>
        int.TryParse(query["purged"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var deleted)
            ? new LogPurgeResult(deleted, int.TryParse(query["failed"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var failed) ? failed : 0)
            : null;

    /// <summary>The services a request uses, resolved once.</summary>
    private sealed record Services(
        LogDirectory Directory, LogRuleStore Rules, LogsOptions Options, LogsPageState State, IAntiforgery Antiforgery,
        TimeProvider Time, IStringLocalizer<LogsStrings> Strings)
    {
        public static Services Of(HttpContext context)
        {
            var provider = context.RequestServices;
            return new Services(
                provider.GetRequiredService<LogDirectory>(),
                provider.GetRequiredService<LogRuleStore>(),
                provider.GetRequiredService<IOptions<LogsOptions>>().Value,
                provider.GetRequiredService<LogsPageState>(),
                provider.GetRequiredService<IAntiforgery>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetRequiredService<IStringLocalizer<LogsStrings>>());
        }
    }
}
