<!-- SPDX-License-Identifier: EUPL-1.2 -->
# OmniEurope.Diagnostics

A Razor class library that adds **a request performance page** and **an application logs page** to an ASP.NET Core
application, kept to the bare minimum: **no theme, no styling, no third-party dependency**, with a collector memory
budget of 2 MiB by default.
A single NuGet package, licensed under EUPL-1.2.

## Usage

```powershell
dotnet add package OmniEurope.Diagnostics
```

```csharp
builder.Services.AddOmniPerformance();                      // collector + what the page needs
app.MapOmniPerformance().RequireAuthorization("Admin");     // the page, on /performance
```

`MapOmniPerformance("/admin/performance")` changes the address. The figures describe the site: protect the page with
the application's authorization policy.

A host that shows the figures itself (an API serving `RequestPerformanceRecorder.Summarize()`, a page in its own
look) registers the collector alone with `AddOmniPerformanceCollector()`, same settings: it gets neither the Razor
component services nor localization. `MapOmniPerformance` then refuses to start, since the page requires
`AddOmniPerformance()`.

## Settings

The `OmniPerformance` section of the host configuration (appsettings, `OmniPerformance__Window` environment
variables...), then `AddOmniPerformance(o => ...)`, which has the last word:

| Setting | Default | Purpose |
|---|---|---|
| `Window` | `7.00:00:00` (7 days) | How far back the figures go, in memory, reset on restart. |
| `SlowestCount` | `20` | How many of the slowest requests of the whole window are listed. |
| `MemoryLimitBytes` | `2097152` (2 MiB) | Memory held by the collector, allocated once at startup. |
| `RouteFilter` | probes, framework and static files left out | Routes measured (code only). |

A setting that leaves nothing to measure (a zero window or count, a window reaching back before the first calendar
date, a limit too low for the chosen window) is refused when the application starts.

## What the package does

- **Server-side collector, no middleware**: `RequestPerformanceRecorder` listens to the duration measurement
  ASP.NET Core already publishes for every request (`http.server.request.duration`), grouped by route template
  (`/orders/{id}`, never the concrete address). Two hosts in one process never mix their figures.
- **Bounded memory**: the limit is shared between the list of the slowest requests (served first) and a ring of
  requests for the percentiles, allocated once: when it is full, the newest request takes the oldest one's place.
  Nothing grows with traffic; a test measures the bytes allocated by a full week and checks they stay under the
  limit. Outside that limit: the route template cache (one entry per application route) and, while the page renders,
  a single copy of the ring for the percentiles, also measured under the limit by a test.
- **Slowest requests first**: the `SlowestCount` slowest requests of the whole window are kept apart from the ring
  (the `SlowestCount` worst of each hour), so a busy site never loses them. One limit: on the oldest hour, partly out
  of the window, a request that was not in its hour's ranking cannot replace an expired one.
- **Per-route percentiles**: call count, median, 95th and 99th percentiles, maximum, computed on the requests in the
  ring; the page states since when they count and when the ring dropped some. Nearest rank, no interpolation: every
  figure is a real request.
- **Page**: a complete HTML document, served by an ordinary endpoint (independent of the application's Blazor
  router) and left out of the measurements, so it never ranks among the slow calls. Texts in French, English served
  according to the request culture (`UseRequestLocalization`).
- **For a live page**: `RequestPerformanceRecorder.WaitForChangeAsync(ct)` completes as soon as a measured request
  has been recorded since the previous wait; the requests arriving between two waits give a single signal, a route
  the filter leaves out gives none. It is meant for one consumer (a background service that tells the open pages and
  spaces its announcements itself): two simultaneous waits share the signals, each one waking a single wait. If the
  pages read the figures through an application endpoint, leave it out of the measurements (`.DisableHttpMetrics()`),
  otherwise every read raises the signal again.

## What it deliberately does not do

- **No theme or styling**: no CSS, no script, no icons, no layout. The markup is semantic (headings, tables) so it
  stays readable without a stylesheet. Styling belongs to the application or its component library, which depends on
  this package, never the other way round.
- **No standalone Blazor WebAssembly**: the collector lives in the server process, the package requires an
  ASP.NET Core host (Blazor Web App, Blazor Server, API).
- Nothing beyond the page: the page provided does not refresh itself (reloading it reads the figures again; the
  signal above serves the application's own pages), no history beyond the window or across a restart, no data sent
  anywhere.

## Logs page

A second page reads the host's JSON log files. The package writes them itself, or the host writes them with
Serilog; the package depends on neither Serilog nor any other logging library.

With the package's own file logger:

```csharp
builder.Logging.AddOmniFileLogging("logs");                 // or the OmniLogs:File section of the configuration
builder.Services.AddOmniLogs();                             // reads the logger's folder when OmniLogs:Directory is empty
app.MapOmniLogs().RequireAuthorization("Admin");            // the pages, under /logs
```

With Serilog, for instance `Serilog.Sinks.File` and `Serilog.Formatting.Compact`:

```csharp
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.File(new CompactJsonFormatter(), "logs/app-.json", rollingInterval: RollingInterval.Day));

builder.Services.AddOmniLogs(o => o.Directory = "logs");    // or the OmniLogs section of the configuration
app.MapOmniLogs().RequireAuthorization("Admin");            // the pages, under /logs
```

**The pages show the application's logs and delete files: they MUST be protected.** `MapOmniLogs` returns a route
group: one `.RequireAuthorization(...)` covers the journal, the download, the rules and the purge.

- **Journal** (`GET /logs`): entries newest first, one page at a time, with a GET filter form (file, minimum level,
  from and to in UTC, text searched in the message, the category, the exception and the correlation id), a detail
  per entry (message, exception, properties, a link to every entry of the same request), follow mode (`?follow=1`,
  the page reloads itself), the download of the selected file and the CSV export of the whole selection.
- **Exclusion rules** (`/logs/rules`): a rule moves to the "excluded lines" tab the entries whose rendered message
  contains a text (ignoring case) and, optionally, whose category is exactly a given one. A rule only filters the
  view, nothing is deleted. The rules are saved as JSON in the rules file of the folder, written atomically.
- **Purge** (`POST /logs/purge`): deletes the log files older than a number of days, after a confirmation box; it
  only touches the files of the folder matching the pattern, never the rules file.
- **Formats**: Serilog's compact JSON (CLEF: `@t`, `@l`, `@mt`, `@m`, `@x`, `@tr` and the properties), the plain
  `JsonFormatter` (`Timestamp`, `Level`, `MessageTemplate`, `RenderedMessage`, `Exception`, `Properties`) and a flat
  JSON line (`Timestamp`, `Level`, `Message`, `SourceContext`, `Exception`, `CorrelationId` and the other
  properties at the top level). Levels are Serilog's names or Microsoft's (`Trace` reads as Verbose, `Critical` as
  Fatal). The
  message is the rendered one when the file has it, otherwise the template with its properties substituted
  (strings without quotes, `{Name:format}` for numbers and dates, invariant culture). A line that is not such an
  event is skipped and counted on the page; a last line still being written is left for the next read.
- **Safety**: a `file` parameter must be a bare name present in the folder listing (no separator, no `..`, no rooted
  path), otherwise the request is refused; every output is HTML-encoded; the CSV export neutralises cells starting
  with `=`, `+`, `-`, `@`; every form carries an antiforgery token that the posts validate themselves
  (`IAntiforgery.IsRequestValidAsync`), so the host needs no `UseAntiforgery()` for these pages (calling it does no
  harm); files are opened shared for writing and deleting, so the sink keeps writing and rolling.

The `OmniLogs` section of the host configuration, then `AddOmniLogs(o => ...)`, which has the last word:

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | required, or the file logger's | The folder the log files are written to; relative to the content root. Only its own files are read. Left empty with `AddOmniFileLogging()`, it is the logger's folder. |
| `FilePattern` | `*.json` | The log files of the folder (file-name wildcards, no path). With the logger's folder, `{FilePrefix}*.json`. |
| `RulesFile` | `omni-log-rules.json` | The rules file, inside the folder; never listed as a log, never purged. |
| `PageSize` | `50` | Entries per page, 1 to 1000. |
| `MaxBytesRead` | `16777216` (16 MiB) | Log text read per request, 4 KiB to 1 GiB: newest files first, a large file from its end; the page says when older entries were left out. |
| `FollowRefreshSeconds` | `5` | Reload period of follow mode, 1 to 3600. |

A missing folder setting or an out-of-range value is refused when the application starts.

### File logger

`builder.Logging.AddOmniFileLogging(...)` adds an `ILoggerProvider` (alias `OmniFile`, so the usual filters apply:
`Logging:OmniFile:LogLevel:Default`) that writes one JSON object per line in the compact Serilog format: `@t`,
`@l`, `@mt`, `@m`, `@x`, `@tr`, `@sp`, the category as `SourceContext`, `EventId`, the entry's properties and its
scopes' properties (ASP.NET Core adds `RequestId`, `RequestPath`...) at the top level. Logging only queues the line:
one background task writes it, so a request never waits for the disk; a full queue or a failed write loses the
entry, counts it and reports the count in one warning line once writing works again. Nothing is ever thrown into
the application. After a failed flush or close the count is an upper bound (the disk may already hold part of
the lines counted), and lines still queued when shutdown stops waiting for the writer (five seconds) are neither
written nor counted.

Files are named `{FilePrefix}[-{Instance}]-yyyyMMdd.json` (UTC day), continued in `.1.json`, `.2.json`... above the
size cap; two processes sharing a folder give themselves different `Instance` values. Retention treats every file
named `{FilePrefix}-...-yyyyMMdd.json` as its own, so prefix `app` also deletes the old files of another
application writing `app-web-...` to the same folder: give each application its own folder, or a prefix that is
not another prefix followed by `-`. The `OmniLogs:File` section,
then `AddOmniFileLogging(o => ...)`, which has the last word:

| Setting | Default | Purpose |
|---|---|---|
| `Directory` | required | The folder the files are written to; relative to the content root. |
| `FilePrefix` | `app` | The start of every file name: letters, digits, `-`, `_`, `.`, 64 at most. |
| `Instance` | none | Added to the file name, cleaned to a host-name-like token. |
| `MinLevel` | `Information` | The lowest level written. |
| `RetentionDays` | `30` | Files of this prefix (every instance, and any prefix that continues it after a `-`) older than that are deleted, at most once an hour; `0` never deletes. |
| `MaxFileBytes` | `104857600` (100 MiB) | Size above which the day continues in the next numbered file, 4 KiB to 1 GiB. |
| `QueueCapacity` | `8192` | Entries waiting to be written, 16 to 1048576. |

### Reading the logs in your own screens

`AddOmniLogs()` also registers what the pages are built on, for a host that shows the logs in its own UI:

- **`ILogJournal`** (scoped): `ListFilesAsync`, `SearchAsync(LogSearch)` (file, minimum level, dates, text, journal
  or excluded tab; every matching entry newest first, within `MaxBytesRead`, with the tab counts, the rules, the
  truncation flag and the unreadable lines), `OpenFileAsync` (a shared read stream, null when the name is not a
  listed file) and `PurgeAsync`. The caller pages and sorts the entries. The `*Async` methods do their file I/O
  synchronously on the calling thread; a host that must not block calls them from a background task.
- **`ILogRuleStore`**: the exclusion rules (load, add, enable or disable, delete). The default keeps them in the
  rules file; a host that registers its own (a database, say), before or after `AddOmniLogs()`, gets it used by the
  pages and by the journal alike.
- **`ILogChanges`**: a `Changed` event raised, at most every two seconds and once more at the end of a busy period,
  when a log file of the folder is created, written, renamed or deleted. The folder is watched only while someone
  listens; a missing folder is looked for again every ten seconds. An exception thrown by a handler is caught and ignored; the other handlers still run.

## Dependencies

- Runtime: the `Microsoft.AspNetCore.App` shared framework only, no third-party package.
- Tests only: `xunit.v3` (Apache-2.0), `bunit` (MIT), `Microsoft.AspNetCore.TestHost` (MIT), `coverlet.MTP` (MIT),
  `Microsoft.Testing.Extensions.TrxReport` (MIT).
- Development only: `dotnet-reportgenerator-globaltool` (Apache-2.0), a local tool pinned in `.config/dotnet-tools.json`
  that merges the coverage reports for `-c`; restored on demand, no global install needed.

## Development

```powershell
.\ylaunch.ps1 -t     # build and run the tests
.\ylaunch.ps1 -c     # coverage, blocking floors, then the CRAP gate
```

`-c` fails under 95 % of lines or 85 % of branches covered (`scripts/coverage-gate.ps1`), then if a method scores
above 30 on CRAP (`scripts/crap-gate.ps1`, justified exceptions in `.config/crap-exceptions.json`, none so far).
Continuous integration applies the same checks.

Before building, the launcher reports, without blocking, a .NET SDK newer than the one in use and outdated NuGet
packages: the repository opens no automatic update request.

Git flow: `main` for released versions, `develop` for integration. Contributors branch `feature/*` from `develop` and
open a pull request to `develop`; the maintainer commits to `develop` directly. A published GitHub release
(tag `1.0.0` or `v1.0.0`, equal to the package version) triggers the NuGet publication of the package validated by
continuous integration.

## Licence

EUPL-1.2, see [LICENSE](https://licenses.nuget.org/EUPL-1.2).
