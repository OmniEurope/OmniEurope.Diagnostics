// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.Logs;

/// <summary>Refuses the <see cref="LogsOptions"/> the page cannot work with, when the host starts.</summary>
internal sealed class LogsOptionsValidator : IValidateOptions<LogsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LogsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Directory))
            failures.Add("OmniLogs:Directory is required: the folder the host's log files are written to (left empty, it defaults to the folder of AddOmniFileLogging()).");
        if (!IsFileNamePattern(options.FilePattern))
            failures.Add($"OmniLogs:FilePattern '{options.FilePattern}' must be a file-name pattern without a path, such as *.json.");
        if (!LogDirectory.IsBareName(options.RulesFile))
            failures.Add($"OmniLogs:RulesFile '{options.RulesFile}' must be a bare file name, without a path.");
        if (options.PageSize is < 1 or > LogsOptions.MaxPageSize)
            failures.Add($"OmniLogs:PageSize {options.PageSize} must be between 1 and {LogsOptions.MaxPageSize}.");
        if (options.MaxBytesRead is < LogsOptions.MinMaxBytesRead or > LogsOptions.MaxMaxBytesRead)
            failures.Add($"OmniLogs:MaxBytesRead {options.MaxBytesRead} must be between {LogsOptions.MinMaxBytesRead} and {LogsOptions.MaxMaxBytesRead} bytes.");
        if (options.FollowRefreshSeconds is < 1 or > LogsOptions.MaxFollowRefreshSeconds)
            failures.Add($"OmniLogs:FollowRefreshSeconds {options.FollowRefreshSeconds} must be between 1 and {LogsOptions.MaxFollowRefreshSeconds}.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>One or more patterns (separated by <c>;</c>) the directory listing applies to file names only:
    /// wildcards, no separator, no parent.</summary>
    private static bool IsFileNamePattern(string? pattern)
    {
        var patterns = LogDirectory.Patterns(pattern);
        return patterns.Count > 0 && patterns.All(one => LogDirectory.IsBareName(one.Replace('*', 'x').Replace('?', 'x')));
    }
}
