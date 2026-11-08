<!-- SPDX-License-Identifier: EUPL-1.2 -->
# OmniEurope.Diagnostics

A Razor class library that adds **a request performance page** to an ASP.NET Core application, kept to the bare
minimum: **no theme, no styling, no third-party dependency**, with a collector memory budget of 2 MiB by default.
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
