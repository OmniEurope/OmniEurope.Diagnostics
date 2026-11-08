// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>Refuses the <see cref="FileLoggingOptions"/> the file logger cannot work with, when the host starts.</summary>
internal sealed class FileLoggingOptionsValidator : IValidateOptions<FileLoggingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FileLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Directory))
            failures.Add("OmniLogs:File:Directory is required: the folder the log files are written to.");
        if (!LogFileNames.IsSafePrefix(options.FilePrefix))
            failures.Add($"OmniLogs:File:FilePrefix '{options.FilePrefix}' must be 1 to {FileLoggingOptions.MaxNameLength} ASCII letters, digits, '-', '_' or '.', not starting with a dot.");
        if (!Enum.IsDefined(options.MinLevel))
            failures.Add($"OmniLogs:File:MinLevel {options.MinLevel} is not a log level.");
        if (options.RetentionDays is < 0 or > FileLoggingOptions.MaxRetentionDays)
            failures.Add($"OmniLogs:File:RetentionDays {options.RetentionDays} must be between 0 and {FileLoggingOptions.MaxRetentionDays}.");
        if (options.MaxFileBytes is < FileLoggingOptions.MinMaxFileBytes or > FileLoggingOptions.MaxMaxFileBytes)
            failures.Add($"OmniLogs:File:MaxFileBytes {options.MaxFileBytes} must be between {FileLoggingOptions.MinMaxFileBytes} and {FileLoggingOptions.MaxMaxFileBytes} bytes.");
        if (options.QueueCapacity is < FileLoggingOptions.MinQueueCapacity or > FileLoggingOptions.MaxQueueCapacity)
            failures.Add($"OmniLogs:File:QueueCapacity {options.QueueCapacity} must be between {FileLoggingOptions.MinQueueCapacity} and {FileLoggingOptions.MaxQueueCapacity}.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
