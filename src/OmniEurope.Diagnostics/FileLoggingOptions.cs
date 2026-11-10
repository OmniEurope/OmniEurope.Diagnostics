// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics;

/// <summary>
/// Where and how the file logger writes the host's logs, in the compact Serilog JSON format (one JSON object per
/// line). Configured through <see cref="FileLoggingBuilderExtensions.AddOmniFileLogging(ILoggingBuilder, Action{FileLoggingOptions}?)"/>
/// (section <c>OmniLogs:File</c>), checked when the host starts: an unusable value stops the host instead of
/// losing the logs.
/// </summary>
public sealed class FileLoggingOptions
{
    /// <summary>The default <see cref="FilePrefix"/>.</summary>
    public const string DefaultFilePrefix = "app";

    /// <summary>The longest <see cref="FilePrefix"/> accepted, and the longest <see cref="Instance"/> kept.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The default <see cref="MinLevel"/>.</summary>
    public const LogLevel DefaultMinLevel = LogLevel.Information;

    /// <summary>The default <see cref="RetentionDays"/>.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>The largest <see cref="RetentionDays"/> accepted: ten years.</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>The default <see cref="MaxFileBytes"/>: 100 MiB.</summary>
    public const long DefaultMaxFileBytes = 100L * 1024 * 1024;

    /// <summary>The smallest <see cref="MaxFileBytes"/> accepted: 4 KiB.</summary>
    public const long MinMaxFileBytes = 4 * 1024;

    /// <summary>The largest <see cref="MaxFileBytes"/> accepted: 1 GiB.</summary>
    public const long MaxMaxFileBytes = 1024L * 1024 * 1024;

    /// <summary>The default <see cref="QueueCapacity"/>.</summary>
    public const int DefaultQueueCapacity = 8192;

    /// <summary>The smallest <see cref="QueueCapacity"/> accepted.</summary>
    public const int MinQueueCapacity = 16;

    /// <summary>The largest <see cref="QueueCapacity"/> accepted.</summary>
    public const int MaxQueueCapacity = 1024 * 1024;

    /// <summary>
    /// Required: the folder the log files are written to, created when missing. A relative path is relative to
    /// the host's content root (<c>IHostEnvironment.ContentRootPath</c>, the application's base directory without
    /// a host).
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>
    /// The start of every file name: <see cref="DefaultFilePrefix"/> by default. ASCII letters, digits, <c>-</c>,
    /// <c>_</c> and <c>.</c> only, not starting with a dot, at most <see cref="MaxNameLength"/> characters. A file
    /// is named <c>{prefix}[-{instance}]-yyyyMMdd.json</c> after its UTC day, then
    /// <c>{prefix}[-{instance}]-yyyyMMdd.1.json</c>, <c>.2</c>... once it reaches <see cref="MaxFileBytes"/>.
    /// </summary>
    public string FilePrefix { get; set; } = DefaultFilePrefix;

    /// <summary>
    /// Optional: the name of this process in the file names (a machine or container name, say), so two processes
    /// sharing a folder never write the same file. Any character other than an ASCII letter, a digit, <c>-</c>,
    /// <c>_</c> or <c>.</c> becomes <c>-</c>, leading and trailing dashes and dots are dropped and it is cut to
    /// <see cref="MaxNameLength"/> characters; nothing left means no instance.
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>
    /// The least severe level written: <see cref="DefaultMinLevel"/> by default; <see cref="LogLevel.None"/>
    /// writes nothing. The host's logging filters (<c>Logging:OmniFile:LogLevel</c>) apply on top of it.
    /// </summary>
    public LogLevel MinLevel { get; set; } = DefaultMinLevel;

    /// <summary>
    /// How many days of files are kept: <see cref="DefaultRetentionDays"/> by default, from 0 to
    /// <see cref="MaxRetentionDays"/>; 0 never deletes. A file whose day in its name is more than this many days
    /// before today (UTC) is deleted, at most once an hour while the logger writes. Only the files named after
    /// <see cref="FilePrefix"/> as above are ever deleted, which includes the files of another application whose
    /// prefix is this one followed by <c>-</c> (prefix <c>app</c> also matches <c>app-web-20261108.json</c>, read as
    /// an instance named <c>web</c>). Give each application its own folder, or a prefix that is not another prefix
    /// followed by <c>-</c>; files that do not follow the naming scheme are never deleted.
    /// </summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// The size, in bytes, above which a day's file is continued in the next numbered file:
    /// <see cref="DefaultMaxFileBytes"/> by default, from <see cref="MinMaxFileBytes"/> to
    /// <see cref="MaxMaxFileBytes"/>. Only a single line larger than this makes a file exceed it.
    /// </summary>
    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>
    /// How many entries wait to be written: <see cref="DefaultQueueCapacity"/> by default, from
    /// <see cref="MinQueueCapacity"/> to <see cref="MaxQueueCapacity"/>. Logging never waits for the disk: when the
    /// queue is full, the entry is dropped and counted, and one warning written afterwards says how many were.
    /// That count after a failed write is an upper bound (the disk may already hold part of the lines counted), and
    /// entries still queued when the host stops waiting for the writer (five seconds at shutdown) are neither written
    /// nor counted.
    /// </summary>
    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    /// <summary>
    /// Optional, code only (not read from the configuration): decides, for each entry that passed the level, whether
    /// it is written; false drops it without counting it as lost. Called on the logging thread before the entry is
    /// formatted, so it must be quick and thread-safe. An entry the filter throws on is written: a broken filter never
    /// hides the logs.
    /// </summary>
    public Func<FileLogCandidate, bool>? Filter { get; set; }
}
