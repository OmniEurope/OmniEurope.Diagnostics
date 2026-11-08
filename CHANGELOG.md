# Changelog

Notable changes to this project are documented here, following the Keep a Changelog format.

## [1.0.0] - 2026-11-08

### Added

- The logs page: `AddOmniLogs()` and `MapOmniLogs()` serve, under `/logs`, the host's Serilog JSON files (compact
  CLEF and plain `JsonFormatter`) as unstyled HTML pages: a journal newest first with filters (file, minimum level,
  dates, text), entry details, follow mode, CSV export and file download; exclusion rules that move matching entries
  to an excluded tab without deleting anything; a purge of old log files. Options in the `OmniLogs` section, checked
  at startup; bounded reading (`MaxBytesRead`, 16 MiB by default); bare listed file names only; antiforgery-checked
  posts. The pages must be protected with `.RequireAuthorization(...)`.

### Fixed

- A `SlowestCount` and a `Window` whose slowest-list reservation overflows are refused at construction instead of
  wrapping around into a huge allocation.
- A `Window` reaching back before the first calendar date is refused at construction instead of throwing on every
  read and on the request path.
- The page's `lang` attribute names the language of its texts: French under a culture without a translation, where it
  used to name that culture's language.
- The package description and the README state 2 MiB as the default collector budget, not as an absolute ceiling.
- The README's licence link no longer renders empty on the NuGet page.

### Changed

- The package continues `OmniEurope.Performance` under a new id, `OmniEurope.Diagnostics`: the package, assembly,
  namespaces, projects and repository are renamed; the performance feature keeps its type names and its
  `/performance` route. `OmniEurope.Performance` is retired step by step. As a new id, the version restarts at
  1.0.0; the earlier entries below are the history of `OmniEurope.Performance`.
- Test dependencies move to their latest stable versions: bUnit 2.14.0, Microsoft.AspNetCore.TestHost 10.0.14,
  Microsoft.Testing.Extensions.TrxReport 2.5.2, xUnit v3 4.0.2.
- The repository records that it uses no Dependabot (`.config/verify-rules.json`): dependencies are updated by hand.
- The .NET SDK floor moves to 10.0.403 (runtime 10.0.14) for CVE-2026-111497.
- The CRAP gate identifies a line by its file's full path across reports and refuses a report without any class.

### Added

- A test of `StopAsync`: the collection stops, and stopping before startup or twice is harmless.

<!-- The entries below are the history of OmniEurope.Performance, the previous package id. -->

## [OmniEurope.Performance 1.1.1] - 2026-10-08

### Fixed

- The percentiles no longer count a request already outside the window when two simultaneous requests reached the
  rolling samples out of order, and the truncation flag no longer goes back on an older overwritten request.
- `MemoryLimitBytes` near `long.MinValue` is refused at construction instead of wrapping around into a huge budget.

### Changed

- ReportGenerator is a pinned local tool, so `.\ylaunch.ps1 -c` no longer needs a global install.
- The coverage floors are defined once, in `scripts/coverage-gate.ps1`.

## [OmniEurope.Performance 1.1.0] - 2026-10-07

### Removed

- `PerformanceReportView` and the public `PerformanceStrings` type: the page renders its figures itself and its texts
  are internal. A host that wants its own look renders `RequestPerformanceRecorder.Summarize()` (breaking change for a
  host that used either type).

## [OmniEurope.Performance 1.0.1] - 2026-10-07

### Added

- Concurrency tests of the change signal: simultaneous requests never throw on the request path and wake the wait;
  two simultaneous waits share the signals, each one waking a single wait.

### Changed

- README, the package page on NuGet, written in English.
- Changelog and repository documentation written in English.

## [OmniEurope.Performance 1.0.0] - 2026-10-07

### Added

- `AddOmniPerformanceCollector`: the collector alone, without the page's services (Razor components, localization),
  for a host that shows the figures itself; `MapOmniPerformance` refuses such a host at startup.
- Blocking coverage floors, in the launcher (`-c`) and continuous integration: 95 % of lines, 85 % of branches.

### Changed

- First stable version: the public API now follows semantic versioning. Compatibility note: 1.1.0 is a historical
  exception to this policy; its Removed section documents the breaking change and migration to `RequestPerformanceRecorder.Summarize()`.

## [OmniEurope.Performance 0.2.0] - 2026-10-06

### Added

- Change signal `RequestPerformanceRecorder.WaitForChangeAsync`: completes as soon as a measured request is recorded,
  several requests between two waits give a single signal; it lets an application page update live.

### Changed

- Package description and README: the 2 MiB limit applies to the memory held by the collector; the README states
  what stays outside that limit (route template cache, copy of the ring while the page renders).

### Fixed

- The default filter leaves out the `/health`, `/_framework`, `/_content` and `/_blazor` families by whole segment:
  `/healthcare/{id}` is measured again.
- The page no longer measures itself: reading it no longer ranks among the slowest calls.
- `<html lang>` is `fr` under the invariant culture (instead of the non-existent `iv` code).
- Rendering the page allocates a single copy of the ring (about 1.9 MiB instead of several MiB): the percentiles are
  read from the copy sorted in place, the ranking comes from a bounded heap.

### Removed

- Automatic dependency update configuration: no more automatic update requests; the local launcher reports outdated
  packages and SDK.

## [OmniEurope.Performance 0.1.0] - 2026-10-06

### Added

- Repository skeleton: solution, `src/OmniEurope.Performance` project, EUPL-1.2 licence, `ylaunch.ps1` launcher,
  CRAP gate (`scripts/crap-gate.ps1`).
- `RequestPerformanceRecorder` collector: request durations per route template, read from the ASP.NET Core
  `http.server.request.duration` measurement, without middleware; 7-day window, at most 2 MiB of memory held, with a
  rolling ring, slowest requests kept outside the ring, `OmniPerformance` settings.
- `/performance` page (`AddOmniPerformance`, `MapOmniPerformance`): unstyled HTML document, slowest calls and
  per-route percentiles; `PerformanceReportView` for a host that styles it. French and English texts.
- Test suite `tests/OmniEurope.Performance.Tests` (xUnit v3, bUnit, ASP.NET Core test host, memory measurement).
- Continuous integration and NuGet publication (`.github/workflows`), `.snupkg` symbol package.

### Changed

- The package references the `Microsoft.AspNetCore.App` shared framework instead of the
  `Microsoft.AspNetCore.Components.Web` package: it requires an ASP.NET Core host.
