// kit-model RepositoryScan 2
// SPDX-License-Identifier: EUPL-1.2
//
// File enumeration for repository-wide guard tests, with one property the raw
// Directory.EnumerateFiles does not have: it refuses to return nothing. A guard that scans the
// repository and finds no file passes vacuously, so an empty scan is reported as a failure.
//
// Build output and nested checkouts (.git, .worktrees) are skipped: a nested
// checkout is another branch's code and changes under the scan. The repository root is the folder
// holding global.json, found by walking up from the test binary, so no path constant is needed.

namespace OmniEurope.Diagnostics.Tests;

internal static class RepositoryScan
{
    /// <summary>The repository root: the nearest ancestor of the test binary holding global.json.</summary>
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repository root (global.json).");
        }
    }

    /// <summary>
    /// Enumerates without a floor, skipping build output and nested checkouts. Only for one leg of a sweep whose total
    /// the caller still proves non-empty.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateOptional(string directory, string pattern)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(Path.DirectorySeparatorChar + Path.GetRelativePath(directory, file)))
                .ToList()
            : [];

    /// <summary>Build output and nested checkouts, never part of a scan. Judged on the path below the scanned
    /// directory: the checkout running the scan may itself live under .worktrees.</summary>
    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}node_modules{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}.git{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}.worktrees{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
