// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Components;

/// <summary>
/// The host's log files as an embeddable, unstyled and interactive Blazor fragment, for a .NET MAUI Blazor Hybrid
/// application or any other Blazor host: the journal and excluded tabs, the filters, one page of entries newest
/// first with their details, the exclusion rules, the purge of old files, a follow mode that reads again when the
/// files change, and, when the host registers an <see cref="ILogFileExport"/>, the CSV export of the selection and
/// the download of the selected file. It talks to the services directly (no HTTP, no query string, no script), so it
/// needs <c>AddOmniLogJournal</c> (and the file logger, <c>AddOmniFileLogging</c>, to have files to read) and
/// localization (<c>AddLocalization</c>). The host places it under its own heading and authorization; its section
/// headings are <c>h2</c>. Controls are drawn only where they can be clicked (an interactive render mode or a Blazor
/// Hybrid web view); static rendering shows the first page of the journal as it was read. A failing service call is
/// shown as text, never thrown out of the component.
/// </summary>
public partial class OmniLogsView : IDisposable
{
    /// <summary>The most days a purge accepts: a hundred years.</summary>
    internal const int MaxPurgeDays = 36500;

    /// <summary>The days the purge starts from.</summary>
    internal const int DefaultPurgeDays = 30;

    private static readonly LogSearch Everything = new() { AllTabs = true };

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopped;
    private readonly HashSet<(string File, DateTimeOffset At, string Message)> _expanded = [];
    private LogsViewFilters _filters = new();
    private LogSearch _applied = Everything;
    private LogsViewTab _tab;
    private int _page = 1;
    private IReadOnlyList<LogFileInfo> _files = [];
    private LogSearchResult _result = new() { Entries = [], JournalCount = 0, ExcludedCount = 0, Rules = [], RulesError = null, Truncated = false, Issues = [] };
    private IReadOnlyList<LogRow> _journal = [];
    private IReadOnlyList<LogRow> _excluded = [];
    private DateTimeOffset _readAt;
    private bool _loaded;
    private bool _loading;
    private ViewMessage? _message;
    private CancellationTokenSource? _search;
    private ILogFileExport? _export;
    private bool _follow;
    private bool _subscribed;
    private volatile bool _disposed;
    private string _pattern = "";
    private string _category = "";
    private int _purgeDays = DefaultPurgeDays;
    private PurgeQuestion? _purgeAsk;
    private LogPurgeResult? _purged;

    /// <summary>Takes the token that ends every call of the view when it is disposed.</summary>
    public OmniLogsView() => _stopped = _lifetime.Token;

    private enum LogsViewTab
    {
        Journal,
        Excluded,
        Rules,
    }

    private sealed record ViewMessage(string Text, bool IsError);

    private sealed record PurgeQuestion(int Days, int Count, long Bytes);

    [Inject] private ILogJournal Journal { get; set; } = default!;
    [Inject] private ILogRuleStore Rules { get; set; } = default!;
    [Inject] private ILogChanges Changes { get; set; } = default!;
    [Inject] private LogDirectory LogFolder { get; set; } = default!;
    [Inject] private IOptions<LogsOptions> Options { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IStringLocalizer<LogsStrings> L { get; set; } = default!;

    private bool Interactive => RendererInfo.IsInteractive;

    private TimeProvider Time => Services.GetService<TimeProvider>() ?? TimeProvider.System;

    private IReadOnlyList<LogRow> CurrentRows => _tab == LogsViewTab.Excluded ? _excluded : _journal;

    private int PageSize => Options.Value.PageSize;

    private int PageCount => Math.Max(1, (CurrentRows.Count + PageSize - 1) / PageSize);

    private int Offset => (_page - 1) * PageSize;

    private IEnumerable<LogRow> PageRows => CurrentRows.Skip(Offset).Take(PageSize);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _export = Services.GetService<ILogFileExport>();
        await ReloadAsync();
    }

    /// <summary>Stops the follow mode and ends any call in progress: nothing is rendered afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unsubscribe();
        _lifetime.Cancel();
        _search?.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Reads the files again with the filters applied; a newer read replaces one in progress.</summary>
    private async Task ReloadAsync()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_stopped);
        _search?.Cancel();
        _search?.Dispose();
        _search = source;
        _loading = true;
        var token = source.Token;
        try
        {
            var files = await Journal.ListFilesAsync(token);
            var result = await Journal.SearchAsync(_applied, token);
            if (!token.IsCancellationRequested)
                Show(files, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced by a newer read, or the view is gone.
        }
        catch (Exception exception)
        {
            _message = new ViewMessage(Failure(exception), IsError: true);
        }
        finally
        {
            _loading &= token.IsCancellationRequested;
        }
    }

    private void Show(IReadOnlyList<LogFileInfo> files, LogSearchResult result)
    {
        _files = files;
        _result = result;
        _journal = result.Entries.Where(row => row.Rule is null).ToList();
        _excluded = result.Entries.Where(row => row.Rule is not null).ToList();
        _page = Math.Min(_page, PageCount);
        _readAt = Time.GetUtcNow();
        _loaded = true;
    }

    /// <summary>Runs a service call; its failure is shown, and the end of the view is not one.</summary>
    private async Task GuardAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(_stopped);
        }
        catch (OperationCanceledException) when (_stopped.IsCancellationRequested)
        {
            // The view is gone.
        }
        catch (Exception exception)
        {
            _message = new ViewMessage(Failure(exception), IsError: true);
        }
    }

    private async Task ApplyAsync()
    {
        if (!_filters.TryToSearch(out var search))
        {
            _message = new ViewMessage(L["InvalidDate"], IsError: true);
            return;
        }
        _applied = search;
        _page = 1;
        _message = null;
        await ReloadAsync();
    }

    private async Task ResetAsync()
    {
        _filters = new LogsViewFilters();
        await ApplyAsync();
    }

    private Task RefreshAsync()
    {
        _message = null;
        return ReloadAsync();
    }

    private async Task SameRequestAsync(string correlationId)
    {
        _filters = new LogsViewFilters { Text = correlationId };
        _tab = LogsViewTab.Journal;
        await ApplyAsync();
    }

    private void ShowTab(LogsViewTab tab)
    {
        _tab = tab;
        _page = 1;
        _message = null;
    }

    private void ToggleDetail(LogEntry entry)
    {
        if (!_expanded.Remove(KeyOf(entry)))
            _expanded.Add(KeyOf(entry));
    }

    private static (string File, DateTimeOffset At, string Message) KeyOf(LogEntry entry) => (entry.File, entry.Timestamp, entry.Message);

    private void Exclude(LogEntry entry)
    {
        _pattern = LogRulePattern.Suggest(entry);
        _category = entry.Category ?? "";
        ShowTab(LogsViewTab.Rules);
    }

    private async Task AddRuleAsync()
    {
        var pattern = _pattern.Trim();
        var category = _category.Trim();
        if (pattern.Length == 0 || pattern.Length > LogRule.MaxLength || category.Length > LogRule.MaxLength)
        {
            _message = new ViewMessage(L["RefusedPattern"], IsError: true);
            return;
        }
        await ChangeRulesAsync(token => Rules.AddAsync(pattern, category.Length == 0 ? null : category, token), "added");
        if (_message is { IsError: false })
            (_pattern, _category) = ("", "");
    }

    private Task ToggleRuleAsync(LogRule rule) =>
        ChangeRulesAsync(token => Rules.SetEnabledAsync(rule.Id, !rule.Enabled, token), rule.Enabled ? "disabled" : "enabled");

    private Task DeleteRuleAsync(LogRule rule) => ChangeRulesAsync(token => Rules.DeleteAsync(rule.Id, token), "deleted");

    /// <summary>Changes the rules, says what came of it, then reads the files again under the new rules.</summary>
    private async Task ChangeRulesAsync(Func<CancellationToken, Task<LogRuleUpdate>> change, string done)
    {
        await GuardAsync(async token => _message = Describe(await change(token), done));
        await ReloadAsync();
    }

    private ViewMessage Describe(LogRuleUpdate update, string done) => update switch
    {
        LogRuleUpdate.Saved => new ViewMessage(L["Done_" + done], IsError: false),
        LogRuleUpdate.NotFound => new ViewMessage(L["RuleNotFound"], IsError: true),
        LogRuleUpdate.Unreadable => new ViewMessage($"{L["RulesUnreadableBefore"]} {LogFolder.RulesLabel} {L["RulesUnreadableAfter"]}", IsError: true),
        _ => new ViewMessage(L["RulesWriteFailed"], IsError: true),
    };

    private void AskPurge()
    {
        _purged = null;
        if (_purgeDays is < 1 or > MaxPurgeDays)
        {
            _message = new ViewMessage(L["RefusedDays"], IsError: true);
            return;
        }
        var now = Time.GetUtcNow();
        var old = _files.Where(file => LogDirectory.IsOlderThan(file.LastWriteUtc.UtcDateTime, _purgeDays, now)).ToList();
        _purgeAsk = new PurgeQuestion(_purgeDays, old.Count, old.Sum(file => file.Length));
        _message = null;
    }

    private void CancelPurge() => _purgeAsk = null;

    private async Task PurgeAsync(PurgeQuestion question)
    {
        _purgeAsk = null;
        await GuardAsync(async token => _purged = await Journal.PurgeAsync(question.Days, token));
        await ReloadAsync();
    }

    private Task ExportCsvAsync(ILogFileExport export) => GuardAsync(async token =>
    {
        const string name = "logs.csv";
        await using var content = new MemoryStream();
        await LogsCsv.WriteAsync(CurrentRows, content, token);
        content.Position = 0;
        await export.ExportAsync(name, "text/csv", content, token);
        _message = new ViewMessage(Format("Exported", name), IsError: false);
    });

    private Task DownloadAsync(ILogFileExport export, string name) => GuardAsync(async token =>
    {
        var source = await Journal.OpenFileAsync(name, token);
        if (source is null)
        {
            _message = new ViewMessage(Format("FileGone", name), IsError: true);
            return;
        }
        await using (source)
            await export.ExportAsync(name, name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "application/json", source, token);
        _message = new ViewMessage(Format("Exported", name), IsError: false);
    });

    private async Task FollowChangedAsync()
    {
        Unsubscribe();
        if (!_follow)
            return;
        Changes.Changed += OnLogsChanged;
        _subscribed = true;
        await ReloadAsync();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        Changes.Changed -= OnLogsChanged;
        _subscribed = false;
    }

    /// <summary>The log files changed: raised on a watcher thread, so the read goes through the renderer's context.</summary>
    private void OnLogsChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
            _ = InvokeAsync(ReloadAndRenderAsync);
    }

    private async Task ReloadAndRenderAsync()
    {
        await ReloadAsync();
        StateHasChanged();
    }

    private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, L[key], values);

    private string Failure(Exception exception) => Format("ServiceFailed", exception.Message);

    private string Tab(string key, int count) => Format(key, LogsFormat.Count(count));

    /// <summary>The level as the reader says it; the enum name stays the value of the select.</summary>
    private string Severity(LogSeverity level) => L["Severity" + level].Value;

    private string IssueText(LogFileIssue issue)
    {
        if (issue.Failed)
            return L["FileFailed"];
        var text = issue.UnreadableCount == 1 ? L["FileUnreadableOne"].Value : Format("FileUnreadable", LogsFormat.Count(issue.UnreadableCount));
        return issue.Lines.Count == 0
            ? text + "."
            : text + " " + Format("UnreadableLines", string.Join(", ", issue.Lines.Select(line => LogsFormat.Count(line)))) + ".";
    }
}
