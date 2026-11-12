<!-- SPDX-License-Identifier: EUPL-1.2 -->
# PLAN-004 - Core, ASP.NET Core and MAUI packages, host-recorded operations, embeddable views

Status: active (2026-11-12), target version 1.2.0 for the three packages.

## Why

A .NET MAUI Blazor Hybrid application has no HTTP pipeline: the request listener hears nothing, the pages need
endpoints that do not exist, and the package's `FrameworkReference` to `Microsoft.AspNetCore.App` forces the
ASP.NET Core shared framework on a desktop application. Such a host wants to time its own operations (reading a
file, loading a screen) and show them, with its logs, inside its own Blazor pages.

## Decisions

- Three packages, all 1.2.0, namespaces unchanged so existing hosts only recompile:
  - `OmniEurope.Diagnostics.Core`: no ASP.NET Core shared framework. Razor class library over the
    `Microsoft.AspNetCore.Components.Web` package and `Microsoft.Extensions.*` packages. Holds the recorder (it
    listens to meters by name, it needs no ASP.NET Core type), its options and summaries, the file logger, the log
    reading services (`ILogJournal`, `ILogRuleStore`, `ILogChanges`), the localized strings, and the new
    embeddable components.
  - `OmniEurope.Diagnostics`: the ASP.NET Core part, depends on Core. Endpoints (`MapOmniPerformance`,
    `MapOmniLogs`), the complete HTML document pages, antiforgery, `AddOmniPerformance` / `AddOmniLogs`.
  - `OmniEurope.Diagnostics.Maui`: MAUI integration, depends on Core, Windows target (the machine and CI have
    the `maui-windows` workload only). Starts what a MAUI host does not start itself (MAUI runs no
    `IHostedService`), defaults the log folder to the application data folder, exports files through the save
    dialog.
- Embeddable components without `<html>`: the host places them in its own `@page`, layout and authorization.
  No routable page is shipped.
- Performance and logs views are both delivered in this plan.

## Scope

1. **Split** (Core + ASP.NET Core): move the host-agnostic code to Core; `OmniEurope.Diagnostics` keeps endpoints,
   `LogQuery` (query string), the endpoint handlers and the document pages. Core has no `FrameworkReference`.
2. **Host-recorded operations** (Core): a direct API on the recorder (record a named operation with its duration
   and outcome; a disposable timing scope), and host instruments named in `PerformanceOptions` (meter, histogram,
   the tag carrying the operation name). Operations share the window, the slowest list and the memory budget with
   requests; the default route filter applies to routes only.
3. **Performance view** (Core): `OmniPerformanceView`, the figures of the page as a fragment, with a refresh; an
   operation shows its outcome instead of an HTTP method and status.
4. **Logs view** (Core): `OmniLogsView`, interactive: filters, paging, detail, same-request entries, excluded tab,
   rules (add, remove), follow mode on `ILogChanges`, purge with confirmation, export and download through an
   export service the host provides (hidden when none is registered).
5. **MAUI package**: one registration call for a MAUI host (file logging in the app data folder by default, log
   journal, performance collector started at app start, file export through the save dialog).
6. **Sample and proof**: a MAUI Blazor Hybrid sample (not packed) timing its own operations and showing both views;
   proof by reading the rendered DOM through the WebView2 remote-debugging port.
7. **CI and docs**: the Ubuntu job builds and tests Core and ASP.NET Core; a Windows job builds the MAUI package and
   sample; the publish workflow packs the three packages. README (three packages, Hybrid usage, dependencies and
   licences) and CHANGELOG.

## Done when

- Core's package has no `FrameworkReference` (checked in the built `.nuspec`), and a MAUI sample with no web server
  shows its own operation timings and its logs in the two views (DOM read over CDP).
- Tests cover the operation API, host instruments, both views (bUnit) and the registrations; `ylaunch.ps1 -c`
  passes (tests, coverage floors, CRAP gate) on both target frameworks.
