// kit-model FileSizeAuditTests 2
// SPDX-License-Identifier: EUPL-1.2
//
// Guard test: a production .cs, .razor or .js file stays within 600 effective lines, so large
// services and components are decomposed into real collaborator types before they turn into god
// files. Blank lines and comments are not counted: documenting a file never costs it its budget.
//
// The production projects are discovered, never listed: every file under the repository's src/
// folder is in scope, so a new project is covered the day it is created.
//
// When this fails, extract a real collaborator class with its own name and responsibility. Never
// split a class across partial files to dodge the budget. A whitelist entry is a policy exception
// that requires the maintainer's explicit approval, with its justification comment.

namespace OmniEurope.Diagnostics.Tests;

public sealed class FileSizeAuditTests
{
    private const int MaxLines = 600;

    // Repo-relative paths, forward slashes, allowed above MaxLines. Every entry carries a
    // justification comment. Empty, and meant to stay that way.
    private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Patterns = ["*.cs", "*.razor", "*.js"];

    [Fact]
    public void NoProductionFile_ExceedsLineBudget()
    {
        var source = Path.Combine(RepositoryScan.Root, "src");
        var files = Patterns
            .SelectMany(pattern => RepositoryScan.EnumerateOptional(source, pattern))
            .Where(file => !IsOutOfScope(file))
            .ToList();
        Assert.True(files.Count > 0, $"No production file found under {source}: the guard would pass vacuously.");

        var offenders = files
            .Select(file => (
                Path: Path.GetRelativePath(RepositoryScan.Root, file).Replace('\\', '/'),
                Lines: EffectiveLines(File.ReadAllLines(file), markup: Path.GetExtension(file).Equals(".razor", StringComparison.OrdinalIgnoreCase))))
            .Where(file => file.Lines > MaxLines && !Whitelist.Contains(file.Path))
            .OrderByDescending(file => file.Lines)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These production files exceed {MaxLines} effective lines; extract real collaborators, never partials:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders.Select(file => $"  - {file.Path} ({file.Lines} effective lines)")));
    }

    [Fact]
    public void EffectiveLines_CountsAnOverrunOfOneLine_AndIgnoresBlankAndCommentLines()
    {
        var code = Enumerable.Repeat("var x = 1;", MaxLines + 1).ToArray();
        string[] noise = ["", "   ", "// a comment", "/// <summary>doc</summary>", "/* block", "still block", "*/"];

        Assert.Equal(MaxLines + 1, EffectiveLines(code, markup: false));
        Assert.Equal(MaxLines + 1, EffectiveLines([.. code, .. noise], markup: false));
        Assert.Equal(MaxLines, EffectiveLines(code[..MaxLines], markup: false));
        Assert.Equal(0, EffectiveLines(["@* razor *@", "<!-- html", "-->"], markup: true));
    }

    // The effective lines of a file: every line that holds something other than a comment. A blank line
    // and a line that is only a comment (// and ///, a /* */ block, and in a .razor file an @* *@ or
    // <!-- --> block as well) are not counted; code followed by a comment is. A block is recognised
    // where it opens a line, so a "/*" inside a string literal never starts one.
    private static int EffectiveLines(string[] lines, bool markup)
    {
        (string Open, string Close)[] blocks = markup
            ? [("/*", "*/"), ("@*", "*@"), ("<!--", "-->")]
            : [("/*", "*/")];
        var count = 0;
        string? closer = null;
        foreach (var line in lines)
        {
            var rest = line.Trim();
            while (true)
            {
                if (closer is not null)
                {
                    var end = rest.IndexOf(closer, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        rest = string.Empty;
                        break;
                    }

                    rest = rest[(end + closer.Length)..].TrimStart();
                    closer = null;
                }

                var block = blocks.FirstOrDefault(candidate => rest.StartsWith(candidate.Open, StringComparison.Ordinal));
                if (block.Open is null)
                {
                    break;
                }

                closer = block.Close;
                rest = rest[block.Open.Length..];
            }

            if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    // Generated code and vendored or minified scripts: their size is not a design signal.
    private static bool IsOutOfScope(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        var name = Path.GetFileName(file);
        return file.Contains($"{separator}Migrations{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}wwwroot{separator}lib{separator}", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase);
    }
}
