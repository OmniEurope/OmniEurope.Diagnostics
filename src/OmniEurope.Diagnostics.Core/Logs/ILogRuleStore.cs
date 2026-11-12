// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Where the exclusion rules live. <c>AddOmniLogs()</c> registers a JSON file in the log folder
/// (<see cref="LogsOptions.RulesFile"/>) unless the host registered its own implementation; a host may also register
/// its own after <c>AddOmniLogs()</c> (a database, say): the last registration is the one resolved, and both the logs
/// pages and <see cref="ILogJournal"/> use it. It may be a singleton or a scoped service (a database context): the
/// pages and the journal resolve it per request scope.
/// </summary>
public interface ILogRuleStore
{
    /// <summary>The rules, in their stored order, or why they could not be read (then none applies).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<LogRuleSet> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds an enabled rule, numbered with an id never used before. The default store throws an <see cref="ArgumentException"/> for a pattern or category longer than <see cref="LogRule.MaxLength"/>.</summary>
    /// <param name="pattern">The text the message contains, at most <see cref="LogRule.MaxLength"/> characters.</param>
    /// <param name="category">The only category it applies to, or null for every category. At most <see cref="LogRule.MaxLength"/> characters.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    Task<LogRuleUpdate> AddAsync(string pattern, string? category, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables the rule <paramref name="id"/>.</summary>
    /// <param name="id">The rule's id.</param>
    /// <param name="enabled">True to enable it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    Task<LogRuleUpdate> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Deletes the rule <paramref name="id"/>.</summary>
    /// <param name="id">The rule's id.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    Task<LogRuleUpdate> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
