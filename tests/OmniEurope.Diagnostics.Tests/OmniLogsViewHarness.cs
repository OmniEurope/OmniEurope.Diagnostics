// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AngleSharp.Dom;
using OmniEurope.Diagnostics.Components;
using OmniEurope.Diagnostics.Logs;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// A bUnit context with the real log services over a real temporary folder, a Blazor Hybrid style interactive
/// renderer and a French culture; what the logs view tests build on.
/// </summary>
internal sealed class OmniLogsViewHarness : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly LogsOptions _options = new();
    private readonly bool _interactive;

    public OmniLogsViewHarness(Action<LogsOptions>? configure = null, TimeProvider? time = null, bool interactive = true)
    {
        _options.Directory = Folder.Path;
        configure?.Invoke(_options);
        // A Blazor Hybrid host has a configuration of its own; the options bind to it.
        Context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Context.Services.AddOmniLogJournal(options =>
        {
            options.Directory = _options.Directory;
            configure?.Invoke(options);
        });
        if (time is not null)
            Context.Services.AddSingleton(time);
        _interactive = interactive;
        UseCulture("fr-FR");
    }

    public TempLogFolder Folder { get; } = new();

    public BunitContext Context { get; } = new();

    /// <summary>The log folder the services were configured with.</summary>
    public LogDirectory Directory => new(Options.Create(_options), environment: null);

    /// <summary>The rule store of the services, built the way the registration builds it.</summary>
    public LogRuleStore RuleStore => new(Directory);

    /// <summary>The journal of the services, built the way the registration builds it.</summary>
    public LogJournal Journal(TimeProvider? time = null) =>
        new(Directory, Options.Create(_options), RuleStore, time ?? TimeProvider.System);

    public void UseCulture(string name) => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    /// <summary>Writes <c>app.json</c> with four entries: two ordinary, a warning and an error of one request.</summary>
    public void SeedApp(string name = "app.json") => Folder.Write(name,
    [
        Clef.Line(Clef.Noon, "Started", "Information", "Host.Boot"),
        Clef.Line(Clef.Noon.AddSeconds(1), "Slow query {Ms} ms", "Warning", "Db", rendered: "Slow query 900 ms", properties: ("CorrelationId", "req-1")),
        Clef.Line(Clef.Noon.AddSeconds(2), "Failed", "Error", "Db", exception: "System.InvalidOperationException: boom", properties: [("CorrelationId", "req-1"), ("Table", "Orders")]),
        Clef.Line(Clef.Noon.AddSeconds(3), "Heartbeat", "Information", "Health"),
    ]);

    /// <summary>Renders the view and waits, within a bounded time, until the files have been read.</summary>
    public IRenderedComponent<OmniLogsView> Render()
    {
        var view = RenderUnread();
        view.WaitForAssertion(() => Assert.DoesNotContain(view.FindAll("section p"),
            paragraph => paragraph.TextContent.StartsWith("Lecture des fichiers", StringComparison.Ordinal)
                || paragraph.TextContent.StartsWith("Reading the files", StringComparison.Ordinal)), Patience);
        return view;
    }

    /// <summary>Renders the view and returns at once, whatever the files' reading came to.</summary>
    public IRenderedComponent<OmniLogsView> RenderUnread()
    {
        // Setting the renderer info resolves services: a test registers its doubles before the first render.
        Context.SetRendererInfo(new RendererInfo("WebView", isInteractive: _interactive));
        return Context.Render<OmniLogsView>();
    }

    /// <summary>The entry rows of the table, newest first (a detail row has one cell and is left out).</summary>
    public static IReadOnlyList<IElement> EntryRows(IRenderedComponent<OmniLogsView> view) =>
        view.Find("section table > tbody").Children.Where(row => row.Children.Length >= 5).ToList();

    /// <summary>The message cell of each entry row.</summary>
    public static string[] Messages(IRenderedComponent<OmniLogsView> view) =>
        [.. EntryRows(view).Select(row => row.Children[3].TextContent)];

    /// <summary>Waits, within a bounded time, for what an asynchronous handler does to come true.</summary>
    public static void Eventually(IRenderedComponent<OmniLogsView> view, Action assertion) =>
        view.WaitForAssertion(assertion, Patience);

    public static IElement Button(IRenderedComponent<OmniLogsView> view, string text) =>
        view.FindAll("button").Single(button => button.TextContent == text);

    /// <summary>Clicks the button with <paramref name="text"/>.</summary>
    public static void Press(IRenderedComponent<OmniLogsView> view, string text) => Button(view, text).Click();

    /// <summary>
    /// Waits, within a bounded time, until the view stops rendering. An asynchronous handler renders more than once
    /// and each render gives the buttons new handlers, so a click that follows a handler waits for it to settle.
    /// This only synchronises the test before its next action: what the view did is asserted separately.
    /// </summary>
    public static void Settle(IRenderedComponent<OmniLogsView> view)
    {
        var deadline = DateTime.UtcNow + Patience;
        string? previous = null;
        var unchanged = 0;
        while (unchanged < 3)
        {
            var current = view.Markup;
            unchanged = current == previous ? unchanged + 1 : 0;
            previous = current;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The view kept rendering.");
            Thread.Sleep(20);
        }
    }

    public static bool HasButton(IRenderedComponent<OmniLogsView> view, string text) =>
        view.FindAll("button").Any(button => button.TextContent == text);

    public void Dispose()
    {
        Context.Dispose();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        Folder.Dispose();
    }
}

/// <summary>An <see cref="ILogFileExport"/> that keeps what it was handed, the content read at once.</summary>
internal sealed class RecordingExport : ILogFileExport
{
    public List<(string Name, string ContentType, byte[] Content)> Exports { get; } = [];

    public Exception? Failure { get; set; }

    public async Task ExportAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        Exports.Add((fileName, contentType, copy.ToArray()));
    }
}

/// <summary>A journal that forwards to the real one unless the test replaces a call.</summary>
internal sealed class JournalDouble(ILogJournal inner) : ILogJournal
{
    public Func<LogSearch, CancellationToken, Task<LogSearchResult>>? OnSearch { get; set; }

    public Func<int, Task<LogPurgeResult>>? OnPurge { get; set; }

    public Func<Task<IReadOnlyList<LogFileInfo>>>? OnList { get; set; }

    public int Searches => Volatile.Read(ref _searches);

    private int _searches;

    public Task<IReadOnlyList<LogFileInfo>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        OnList?.Invoke() ?? inner.ListFilesAsync(cancellationToken);

    public Task<LogSearchResult> SearchAsync(LogSearch search, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _searches);
        return OnSearch?.Invoke(search, cancellationToken) ?? inner.SearchAsync(search, cancellationToken);
    }

    public Task<Stream?> OpenFileAsync(string name, CancellationToken cancellationToken = default) =>
        inner.OpenFileAsync(name, cancellationToken);

    public Task<LogPurgeResult> PurgeAsync(int olderThanDays, CancellationToken cancellationToken = default) =>
        OnPurge?.Invoke(olderThanDays) ?? inner.PurgeAsync(olderThanDays, cancellationToken);
}

/// <summary>A rule store that answers what the test says, or forwards to the real one.</summary>
internal sealed class RuleStoreDouble(ILogRuleStore inner) : ILogRuleStore
{
    public LogRuleUpdate? Answer { get; set; }

    public Exception? Failure { get; set; }

    public Task<LogRuleSet> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

    public Task<LogRuleUpdate> AddAsync(string pattern, string? category, CancellationToken cancellationToken = default) =>
        Reply() ?? inner.AddAsync(pattern, category, cancellationToken);

    public Task<LogRuleUpdate> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) =>
        Reply() ?? inner.SetEnabledAsync(id, enabled, cancellationToken);

    public Task<LogRuleUpdate> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        Reply() ?? inner.DeleteAsync(id, cancellationToken);

    private Task<LogRuleUpdate>? Reply() =>
        Failure is not null ? Task.FromException<LogRuleUpdate>(Failure) : Answer is { } answer ? Task.FromResult(answer) : null;
}

/// <summary>A change signal the test raises, counting its subscribers; it can also keep a handler that unsubscribed.</summary>
internal sealed class ManualChanges(bool forgetsUnsubscriptions = false) : ILogChanges
{
    private EventHandler? _handlers;

    public int Subscribers { get; private set; }

    public event EventHandler? Changed
    {
        add
        {
            _handlers += value;
            Subscribers++;
        }
        remove
        {
            Subscribers--;
            if (!forgetsUnsubscriptions)
                _handlers -= value;
        }
    }

    public void Raise() => _handlers?.Invoke(this, EventArgs.Empty);
}
