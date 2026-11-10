// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics;

/// <summary>
/// Where the logs page reads the host's Serilog files, where it keeps its exclusion rules and how much it reads
/// per request. Configured through
/// <see cref="Microsoft.Extensions.DependencyInjection.LogsServiceCollectionExtensions.AddOmniLogs"/> (section
/// <c>OmniLogs</c>), checked when the host starts: an unusable value stops the host instead of failing on the page.
/// </summary>
public sealed class LogsOptions
{
    /// <summary>The default <see cref="FilePattern"/>: every JSON file of <see cref="Directory"/>.</summary>
    public const string DefaultFilePattern = "*.json";

    /// <summary>The default <see cref="RulesFile"/>.</summary>
    public const string DefaultRulesFile = "omni-log-rules.json";

    /// <summary>The default <see cref="MaxBytesRead"/>: 16 MiB.</summary>
    public const long DefaultMaxBytesRead = 16L * 1024 * 1024;

    /// <summary>The smallest <see cref="MaxBytesRead"/> accepted: 4 KiB.</summary>
    public const long MinMaxBytesRead = 4 * 1024;

    /// <summary>The largest <see cref="MaxBytesRead"/> accepted: 1 GiB.</summary>
    public const long MaxMaxBytesRead = 1024L * 1024 * 1024;

    /// <summary>The largest <see cref="PageSize"/> accepted.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>The largest <see cref="FollowRefreshSeconds"/> accepted: one hour.</summary>
    public const int MaxFollowRefreshSeconds = 3600;

    /// <summary>
    /// Required: the folder the host's Serilog file sink writes to. A relative path is relative to the host's
    /// content root (<c>IHostEnvironment.ContentRootPath</c>). Only the files directly in it are read, never a
    /// sub-folder.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// The log files of <see cref="Directory"/>, as one or more file-name patterns separated by <c>;</c> (<c>*</c> and
    /// <c>?</c> wildcards, no path; <c>*.json;*.log</c>, say): <see cref="DefaultFilePattern"/> by default. Only these
    /// files are listed, read, downloaded and purged. A file ending in <c>.log</c> is read as plain text: every
    /// non-empty line is an entry, its whole text the message, level Information, timestamped by the ISO date it
    /// starts with, else by the previous line's, else by the file's last write.
    /// </summary>
    public string FilePattern { get; set; } = DefaultFilePattern;

    /// <summary>
    /// The file name, inside <see cref="Directory"/>, the exclusion rules are saved to:
    /// <see cref="DefaultRulesFile"/> by default. It is never listed as a log file and never purged.
    /// </summary>
    public string RulesFile { get; set; } = DefaultRulesFile;

    /// <summary>How many entries a page lists: 50 by default, from 1 to <see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// The most log text one request reads, in bytes: <see cref="DefaultMaxBytesRead"/> by default, from
    /// <see cref="MinMaxBytesRead"/> to <see cref="MaxMaxBytesRead"/>. The newest files are read first and a file
    /// larger than what is left is read from its end, so the cap always keeps the most recent entries; the page
    /// says when older ones were left out. A request holds about this much text plus its parsed entries in
    /// memory, so a huge log cannot exhaust the host.
    /// </summary>
    public long MaxBytesRead { get; set; } = DefaultMaxBytesRead;

    /// <summary>How often the page reloads itself in follow mode (<c>?follow=1</c>), in seconds: 5 by default,
    /// from 1 to <see cref="MaxFollowRefreshSeconds"/>.</summary>
    public int FollowRefreshSeconds { get; set; } = 5;
}
