<!-- SPDX-License-Identifier: EUPL-1.2 -->
# PLAN-002 - File logging and a public log reader

Status: done (implemented on develop, version 1.0.1, 2026-11-09). Target version: 1.0.1 (also ships the `[Unreleased]` fixes).

## Why

The logs page only reads files a host writes itself with Serilog, and everything behind it is internal. A host that
wants its own log screen (its own UI, its own rule storage, live updates) has to copy the reading code, and a host
without a log writer has nothing to show. The package therefore offers both sides, with one format.

## Scope

1. **Writer**: `ILoggingBuilder.AddOmniFileLogging(...)` registers an `ILoggerProvider` (alias `OmniFile`) that writes
   the compact Serilog JSON format (CLEF: `@t`, `@l`, `@mt`, `@m`, `@x`, `@tr`, structured properties and scope
   properties at the top level), one file per day and instance (`{prefix}[-{instance}]-yyyyMMdd.json`, rolled to
   `.1`, `.2`... above a size cap), through a bounded background queue: logging never blocks a request, a saturated
   queue drops and counts, then writes one warning. Optional retention in days. Options `FileLoggingOptions`
   (section `OmniLogs:File`). No Serilog dependency.
2. **Reader**: public `ILogJournal`: list the files, search the entries (file, minimum level, dates, text, journal or
   excluded tab; every matching entry newest first, bounded by `MaxBytesRead`, with the truncation flag and the
   unreadable-line issues), open a file as a stream, purge old files; every call takes a `CancellationToken`.
   Public `LogEntry`, `LogSeverity`, `LogRule` and the search and result types.
3. **Rules**: public `ILogRuleStore` (load, add, enable or disable, delete), the JSON file implementation by default;
   a host registers its own (a database, say) before or after `AddOmniLogs()` and both the page and the journal use it.
4. **Change signal**: public `ILogChanges` raising `Changed` (throttled) when a log file of the folder is created,
   written, renamed or deleted, for live screens.
5. **Pages**: the HTML pages become a client of `ILogJournal` and `ILogRuleStore`, same behaviour and same tests.
6. **Formats read**: CLEF, Serilog's `JsonFormatter`, and a flat JSON format (`Timestamp`, `Level`, `Message`,
   `SourceContext`, `Exception`, `CorrelationId` at the top level) so files of a simple custom writer read too.
7. **Defaults**: when `OmniLogs:Directory` is empty and the writer is registered, the reader uses the writer's folder
   and file prefix.

## Done when

- A host calling `builder.Logging.AddOmniFileLogging(...)`, `AddOmniLogs()` and `MapOmniLogs()` sees an error it
  logged on the page (real host, real browser).
- A test host replaces `ILogRuleStore` and the page and the journal apply its rules.
- `ylaunch.ps1 -c` passes (tests, coverage floors, CRAP gate), README and CHANGELOG describe the new API, version 1.0.1.
