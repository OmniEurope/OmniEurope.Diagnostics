// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>
/// Points the logs pages at the file logger's folder when the host names none: an empty
/// <see cref="LogsOptions.Directory"/> takes <see cref="FileLoggingOptions.Directory"/> (both relative to the content
/// root), and the default <see cref="LogsOptions.FilePattern"/> narrows to the logger's own files
/// (<c>{prefix}*.json</c>). A folder the host sets keeps every value it was given.
/// </summary>
internal sealed class LogsOptionsFromFileLogging(IOptions<FileLoggingOptions> fileLogging) : IPostConfigureOptions<LogsOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, LogsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Directory))
            return;
        var writer = fileLogging.Value;
        if (string.IsNullOrWhiteSpace(writer.Directory))
            return;
        options.Directory = writer.Directory;
        if (options.FilePattern == LogsOptions.DefaultFilePattern)
            options.FilePattern = writer.FilePrefix + "*.json";
    }
}
