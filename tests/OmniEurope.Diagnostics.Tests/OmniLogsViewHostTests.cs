// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Diagnostics.Components;
using OmniEurope.Diagnostics.Logs;
using static OmniEurope.Diagnostics.Tests.OmniLogsViewHarness;

namespace OmniEurope.Diagnostics.Tests;

/// <summary>
/// What the logs view does for its host: following the files, leaving no subscription behind, handing exports to
/// the host's <see cref="ILogFileExport"/>, and showing a failing service as text.
/// </summary>
public sealed class OmniLogsViewHostTests : IDisposable
{
    private const string ExportCsv = "Exporter la sélection (CSV)";
    private const string DownloadFile = "Télécharger le fichier";
    private readonly OmniLogsViewHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static string Alert(IRenderedComponent<OmniLogsView> view) => view.Find("p[role=alert]").TextContent;

    private static void Follow(IRenderedComponent<OmniLogsView> view, bool on) => view.Find("input[type=checkbox]").Change(on);

    [Fact]
    public void FollowMode_ReadsTheFilesAgain_WhenARealFileIsAppendedTo_AndStopsWatchingWhenSwitchedOff()
    {
        _harness.SeedApp();
        var view = _harness.Render();
        var watcher = (LogChangeWatcher)_harness.Context.Services.GetRequiredService<ILogChanges>();
        Assert.False(watcher.Watching);

        Follow(view, on: true);

        Eventually(view, () => Assert.True(watcher.Watching));
        Assert.Contains("Suivi en direct : la liste se met à jour dès que les fichiers changent.", view.Markup, StringComparison.Ordinal);
        File.AppendAllText(_harness.Folder.File("app.json"), Clef.Line(Clef.Noon.AddMinutes(1), "Appended live", "Information", "Live") + "\n");
        Eventually(view, () => Assert.Equal("Appended live", Messages(view)[0]));

        Follow(view, on: false);

        Eventually(view, () => Assert.False(watcher.Watching));
        Assert.DoesNotContain("Suivi en direct", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARaisedChange_SearchesAgain_AndDisposingUnsubscribes_WithNothingLeftAfterwards()
    {
        _harness.SeedApp();
        var changes = new ManualChanges();
        var journal = new JournalDouble(_harness.Journal());
        _harness.Context.Services.AddSingleton<ILogChanges>(changes);
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);
        var view = _harness.Render();
        Assert.Equal(1, journal.Searches);

        Follow(view, on: true);
        Eventually(view, () => Assert.Equal(1, changes.Subscribers));
        Eventually(view, () => Assert.Equal(2, journal.Searches));
        File.AppendAllText(_harness.Folder.File("app.json"), Clef.Line(Clef.Noon.AddMinutes(1), "Raised", "Information", "Live") + "\n");
        changes.Raise();

        Eventually(view, () =>
        {
            Assert.Equal(3, journal.Searches);
            Assert.Equal("Raised", Messages(view)[0]);
        });
        view.Instance.Dispose();
        Assert.Equal(0, changes.Subscribers);
        changes.Raise();
        Assert.Equal(3, journal.Searches);
        Assert.Null(Record.Exception(view.Instance.Dispose));
    }

    [Fact]
    public void AChangeRaisedAfterDisposal_ByAHandlerThatWasNotRemoved_IsIgnored()
    {
        _harness.SeedApp();
        var changes = new ManualChanges(forgetsUnsubscriptions: true);
        var journal = new JournalDouble(_harness.Journal());
        _harness.Context.Services.AddSingleton<ILogChanges>(changes);
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);
        var view = _harness.Render();
        Follow(view, on: true);
        Eventually(view, () => Assert.Equal(2, journal.Searches));
        view.Instance.Dispose();

        var exception = Record.Exception(changes.Raise);

        Assert.Null(exception);
        Assert.Equal(2, journal.Searches);
    }

    [Fact]
    public async Task ASearchInProgress_IsCancelled_WhenTheViewIsDisposed_AndASupersededOneIsNotAnError()
    {
        _harness.SeedApp();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new JournalDouble(_harness.Journal());
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);
        var view = _harness.Render();
        journal.OnSearch = async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        };

        Press(view, "Actualiser");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        journal.OnSearch = null;
        Press(view, "Actualiser");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        Eventually(view, () => Assert.Equal(4, Messages(view).Length));
        Assert.Empty(view.FindAll("p[role=alert]"));

        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.OnSearch = async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        };
        Press(view, "Actualiser");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);

        view.Instance.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AServiceThatFails_IsShownAsText_AndTheNextRefreshRecovers()
    {
        _harness.SeedApp();
        var journal = new JournalDouble(_harness.Journal()) { OnSearch = (_, _) => throw new InvalidOperationException("boom") };
        _harness.Context.Services.AddSingleton<ILogJournal>(journal);

        var view = _harness.RenderUnread();

        Eventually(view, () => Assert.Equal("L'opération a échoué : boom", Alert(view)));
        Assert.Empty(view.FindAll("table"));
        Assert.DoesNotContain("Lecture des fichiers en cours", view.Markup, StringComparison.Ordinal);
        journal.OnSearch = null;
        journal.OnList = () => throw new IOException("no listing");
        Press(view, "Actualiser");
        Eventually(view, () => Assert.Equal("L'opération a échoué : no listing", Alert(view)));
        journal.OnList = null;

        Press(view, "Actualiser");

        Eventually(view, () => Assert.Equal(4, Messages(view).Length));
        Assert.Empty(view.FindAll("p[role=alert]"));
    }

    [Fact]
    public void WithoutAnExporter_NeitherTheExportNorTheDownloadIsOffered()
    {
        _harness.SeedApp();
        var view = _harness.Render();

        view.Find("#omni-logs-file").Change("app.json");
        view.Find("form[role=search]").Submit();

        Eventually(view, () => Assert.Equal(4, Messages(view).Length));
        Assert.False(HasButton(view, ExportCsv));
        Assert.False(HasButton(view, DownloadFile));
    }

    [Fact]
    public async Task TheExport_HandsTheCsvOfTheCurrentTabToTheHost_WithFormulasNeutralised()
    {
        _harness.Folder.Write("app.json",
        [
            Clef.Line(Clef.Noon, "=cmd|calc", "Information", "Cat"),
            Clef.Line(Clef.Noon.AddSeconds(1), "Heartbeat, \"quoted\"", "Warning", "Health"),
        ]);
        await _harness.RuleStore.AddAsync("Heartbeat", null, Xunit.TestContext.Current.CancellationToken);
        var export = new RecordingExport();
        _harness.Context.Services.AddSingleton<ILogFileExport>(export);
        var view = _harness.Render();
        Assert.False(HasButton(view, DownloadFile));

        Press(view, ExportCsv);

        Eventually(view, () => Assert.Equal("Fichier transmis : logs.csv.", view.Find("p[role=status]").TextContent));
        var (name, contentType, content) = Assert.Single(export.Exports);
        Assert.Equal(("logs.csv", "text/csv"), (name, contentType));
        var text = Encoding.UTF8.GetString(content);
        Assert.StartsWith("﻿\"Timestamp\",\"Level\",\"Category\",\"Message\"", text, StringComparison.Ordinal);
        Assert.Contains("\"'=cmd|calc\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("quoted", text, StringComparison.Ordinal);

        Press(view, "Lignes exclues (1)");
        Eventually(view, () => Assert.Equal(["Heartbeat, \"quoted\""], Messages(view)));
        Press(view, ExportCsv);

        Eventually(view, () => Assert.Equal(2, export.Exports.Count));
        var excluded = Encoding.UTF8.GetString(export.Exports[1].Content);
        Assert.Contains("\"Heartbeat, \"\"quoted\"\"\"", excluded, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd|calc", excluded, StringComparison.Ordinal);
        Assert.EndsWith("\"Heartbeat\"\r\n", excluded, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDownload_HandsTheSelectedFileAsItIsOnDisk_WithATypeByExtension()
    {
        using var mixed = new OmniLogsViewHarness(options => options.FilePattern = "*.json;*.log");
        mixed.SeedApp();
        File.WriteAllText(mixed.Folder.File("plain.log"), "2026-11-08T12:00:00Z plain line\n");
        var export = new RecordingExport();
        mixed.Context.Services.AddSingleton<ILogFileExport>(export);
        var view = mixed.Render();

        view.Find("#omni-logs-file").Change("app.json");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.True(HasButton(view, DownloadFile)));
        Settle(view);
        Press(view, DownloadFile);
        Eventually(view, () => Assert.Equal("Fichier transmis : app.json.", view.Find("p[role=status]").TextContent));
        Settle(view);

        view.Find("#omni-logs-file").Change("plain.log");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.Equal(["2026-11-08T12:00:00Z plain line"], Messages(view)));
        Settle(view);
        Press(view, DownloadFile);

        Eventually(view, () => Assert.Equal(2, export.Exports.Count));
        Assert.Equal(("app.json", "application/json"), (export.Exports[0].Name, export.Exports[0].ContentType));
        Assert.Equal(File.ReadAllBytes(mixed.Folder.File("app.json")), export.Exports[0].Content);
        Assert.Equal(("plain.log", "text/plain"), (export.Exports[1].Name, export.Exports[1].ContentType));
        Assert.Equal("Fichier transmis : plain.log.", view.Find("p[role=status]").TextContent);
    }

    [Fact]
    public void ADownloadOfAFileThatIsGone_AndAnExporterThatFails_AreShownAsText()
    {
        _harness.SeedApp();
        var export = new RecordingExport();
        _harness.Context.Services.AddSingleton<ILogFileExport>(export);
        var view = _harness.Render();
        view.Find("#omni-logs-file").Change("app.json");
        view.Find("form[role=search]").Submit();
        Eventually(view, () => Assert.True(HasButton(view, DownloadFile)));
        Settle(view);

        File.Delete(_harness.Folder.File("app.json"));
        Press(view, DownloadFile);

        Eventually(view, () => Assert.Equal("Le fichier app.json n'existe plus.", Alert(view)));
        Assert.Empty(export.Exports);
        Settle(view);

        export.Failure = new InvalidOperationException("the user cancelled");
        Press(view, ExportCsv);

        Eventually(view, () => Assert.Equal("L'opération a échoué : the user cancelled", Alert(view)));
    }
}
