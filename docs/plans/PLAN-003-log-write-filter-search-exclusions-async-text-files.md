<!-- SPDX-License-Identifier: EUPL-1.2 -->
# PLAN-003 - Write filter, search exclusions, asynchronous reading and plain-text files

Status: in progress on develop (2026-11-10). Target version: not decided.

## Why

A host that moved its own log screen onto `ILogJournal` still keeps code of its own for four gaps: it drops noisy
entries while writing, it applies per-tenant exclusions after the search (so its counts and pages are wrong), it wraps
the synchronous reading in `Task.Run`, and it cannot show older plain-text `.log` files.

## Scope

1. **Write filter**: `FileLoggingOptions.Filter`, a `Func<FileLogCandidate, bool>` called on the caller's thread
   before an entry is formatted; false drops it (not counted as lost). A filter that throws keeps the entry.
2. **Search exclusions**: `LogSearch.Exclusions`, a list of `LogExclusion` (category and/or message, "contains",
   case ignored). An entry one of them matches is left out of the result before anything is counted: not in the
   entries, not in `JournalCount` nor `ExcludedCount`.
3. **Asynchronous reading**: `SearchAsync` reads the files with asynchronous streams, `OpenFileAsync` returns a
   stream opened for asynchronous I/O, the default rule store reads its file asynchronously. Listing and deleting
   files stay synchronous: the operating system offers no asynchronous call for them (metadata only, no content read).
4. **Plain-text files**: `LogsOptions.FilePattern` accepts several patterns separated by `;` (`*.json;*.log`). A file
   ending in `.log` is read as text: every non-empty line is an entry, its message the line itself, level
   Information, its timestamp the one the line starts with when it has one, else the previous line's, else the
   file's last write time.

## Done when

- Tests over real files and a real host cover each item (filter drops and keeps, exclusions change counts, a search
  cancelled mid-read, a `.log` file listed, searched and its timestamps read).
- `ylaunch.ps1 -c` passes (tests, coverage floors, CRAP gate); README and CHANGELOG describe the additions.
