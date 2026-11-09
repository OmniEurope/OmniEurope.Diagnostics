// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// An exclusion rule: the entries whose rendered message contains <paramref name="Pattern"/> (ignoring case) and,
/// when <paramref name="Category"/> is set, whose category is exactly it (ignoring case) leave the journal for the
/// excluded tab while the rule is enabled. A rule only filters the view: nothing is deleted.
/// </summary>
/// <param name="Id">The rule's number, unique in the file.</param>
/// <param name="Pattern">The text the message contains.</param>
/// <param name="Category">The only category it applies to, or null for every category.</param>
/// <param name="Enabled">Whether it excludes anything.</param>
public sealed record LogRule(int Id, string Pattern, string? Category, bool Enabled)
{
    /// <summary>The longest pattern or category a rule accepts.</summary>
    public const int MaxLength = 1000;

    /// <summary>Whether the rule's text and category describe <paramref name="entry"/>, enabled or not.</summary>
    /// <param name="entry">The entry tested.</param>
    public bool Matches(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Message.Contains(Pattern, StringComparison.OrdinalIgnoreCase)
            && (Category is null || string.Equals(entry.Category, Category, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The rules as read: the list, or why they could not be read (then none applies).</summary>
/// <param name="Rules">The rules, in stored order.</param>
/// <param name="Error">Null when the rules were read (an absent file has none); the reason otherwise.</param>
public sealed record LogRuleSet(IReadOnlyList<LogRule> Rules, string? Error)
{
    /// <summary>The id the next rule of the file gets: ids are never reused, so a stale form never reaches a newer rule.</summary>
    internal int NextId { get; init; } = 1;
}

/// <summary>What a change to the rules came to.</summary>
public enum LogRuleUpdate
{
    /// <summary>Saved.</summary>
    Saved,

    /// <summary>No rule has the id the change named; nothing was written.</summary>
    NotFound,

    /// <summary>The stored rules exist but cannot be read: they are never overwritten, nothing was written.</summary>
    Unreadable,

    /// <summary>The file could not be written (read-only folder, full disk, locked file): the old one is kept.</summary>
    WriteFailed,
}

/// <summary>
/// The default <see cref="ILogRuleStore"/>: the rules file of <see cref="LogsOptions.RulesFile"/>, as JSON. Every
/// change reads, changes and writes the file under one lock, so concurrent posts never lose a rule; it is written to
/// a temporary file of the same folder and moved over the old one, so a reader never sees half a file. An unreadable
/// file is reported, never replaced. The file is small and read synchronously: the asynchronous methods only check
/// their token before the work.
/// </summary>
internal sealed class LogRuleStore(LogDirectory directory) : ILogRuleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    /// <summary>Reads the rules; an absent file has none.</summary>
    public LogRuleSet Load()
    {
        if (!File.Exists(directory.RulesPath))
            return new LogRuleSet([], null);
        try
        {
            using var stream = LogFileReader.OpenShared(directory.RulesPath);
            var document = JsonSerializer.Deserialize<RulesDocument>(stream, Json);
            var rules = (document?.Rules ?? []).Where(rule => !string.IsNullOrWhiteSpace(rule?.Pattern)).ToList();
            var next = Math.Max(document?.NextId ?? 1, AfterLast(rules));
            return new LogRuleSet(rules, null) { NextId = next };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LogRuleSet([], exception.Message);
        }
    }

    /// <inheritdoc />
    public Task<LogRuleUpdate> AddAsync(string pattern, string? category, CancellationToken cancellationToken = default) =>
        Run(() => Add(pattern, category), cancellationToken);

    /// <inheritdoc />
    public Task<LogRuleUpdate> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default) =>
        Run(() => SetEnabled(id, enabled), cancellationToken);

    /// <inheritdoc />
    public Task<LogRuleUpdate> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        Run(() => Delete(id), cancellationToken);

    /// <inheritdoc />
    public Task<LogRuleSet> LoadAsync(CancellationToken cancellationToken = default) => Run(Load, cancellationToken);

    private static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken) => SyncTask.Run(work, cancellationToken);

    /// <summary>Adds a rule, enabled, numbered after the highest id.</summary>
    public LogRuleUpdate Add(string pattern, string? category)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > LogRule.MaxLength || category?.Length > LogRule.MaxLength)
            throw new ArgumentException($"The pattern and the category are at most {LogRule.MaxLength} characters.", nameof(pattern));
        return AddRule(pattern, category);
    }

    private LogRuleUpdate AddRule(string pattern, string? category) =>
        Update((rules, next) =>
        {
            rules.Add(new LogRule(next, pattern, category, Enabled: true));
            return true;
        });

    /// <summary>Enables or disables the rule <paramref name="id"/>.</summary>
    public LogRuleUpdate SetEnabled(int id, bool enabled) =>
        Update((rules, _) =>
        {
            var index = rules.FindIndex(rule => rule.Id == id);
            if (index < 0)
                return false;
            rules[index] = rules[index] with { Enabled = enabled };
            return true;
        });

    /// <summary>Deletes the rule <paramref name="id"/>.</summary>
    public LogRuleUpdate Delete(int id) => Update((rules, _) => rules.RemoveAll(rule => rule.Id == id) > 0);

    private LogRuleUpdate Update(Func<List<LogRule>, int, bool> change)
    {
        lock (_gate)
        {
            var set = Load();
            if (set.Error is not null)
                return LogRuleUpdate.Unreadable;
            var rules = set.Rules.ToList();
            if (!change(rules, set.NextId))
                return LogRuleUpdate.NotFound;
            return Save(rules, Math.Max(set.NextId, AfterLast(rules))) ? LogRuleUpdate.Saved : LogRuleUpdate.WriteFailed;
        }
    }

    private bool Save(List<LogRule> rules, int nextId)
    {
        var temporary = Path.Combine(directory.FullPath, $"{directory.RulesFile}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory.FullPath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new RulesDocument(nextId, rules), Json));
            File.Move(temporary, directory.RulesPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            // Left behind only when the move failed: the rules file prefix keeps it out of the purge, so it goes now.
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static int AfterLast(List<LogRule> rules) => rules.Select(rule => rule.Id).DefaultIfEmpty(0).Max() + 1;

    private sealed record RulesDocument(int? NextId, List<LogRule>? Rules);
}
