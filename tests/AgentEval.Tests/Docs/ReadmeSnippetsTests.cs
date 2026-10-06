// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Xunit;

namespace AgentEval.Tests.Docs;

/// <summary>
/// Drift gate for the C# examples in the repository <c>README.md</c>. Every C# code block there must be
/// preceded by a <c>&lt;!-- snippet: name --&gt;</c> marker, and its text must equal the
/// <c>// begin-snippet: name</c> … <c>// end-snippet</c> region of the same name in
/// <c>samples/AgentEval.ReadmeSnippets</c>, a project in <c>AgentEval.sln</c> that is compiled with every build.
/// Together the two mean a README example can neither name a type or member that does not exist (the sample
/// would not compile) nor drift away from the code that does compile (this test fails). The README once showed
/// <c>AzureModelFactory</c>, <c>ComparisonOptions</c> and <c>HallucinationDetectedException</c>, none of which
/// existed anywhere in the repository.
/// </summary>
public class ReadmeSnippetsTests
{
    private const string SnippetProjectFolder = "AgentEval.ReadmeSnippets";

    private static readonly Regex s_markerRegex = new(
        @"^<!--\s*snippet:\s*(?<name>[A-Za-z0-9_.-]+)\s*-->$", RegexOptions.Compiled);

    private static readonly Regex s_beginRegex = new(
        @"^//\s*begin-snippet:\s*(?<name>[A-Za-z0-9_.-]+)$", RegexOptions.Compiled);

    private static readonly Regex s_endRegex = new(@"^//\s*end-snippet$", RegexOptions.Compiled);

    [Fact]
    public void EveryCSharpBlockInTheReadme_HasASnippetMarker()
    {
        var (blocks, problems) = ParseReadme();

        Assert.NotEmpty(blocks);
        Assert.True(problems.Count == 0,
            "README.md has C# blocks or snippet markers that do not pair up. Put `<!-- snippet: <name> -->` on the " +
            "line before every ```csharp block, and give it a `// begin-snippet: <name>` region in " +
            $"samples/{SnippetProjectFolder}:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void EveryReadmeSnippet_MatchesItsCompiledRegion()
    {
        var (blocks, _) = ParseReadme();
        var named = blocks.Where(b => b.Name is not null).ToList();
        Assert.NotEmpty(named);

        var (regions, regionProblems) = ParseSampleRegions();
        Assert.True(regionProblems.Count == 0,
            $"samples/{SnippetProjectFolder} has malformed snippet regions:\n  " + string.Join("\n  ", regionProblems));

        var mismatches = new List<string>();
        foreach (var block in named)
        {
            if (!regions.TryGetValue(block.Name!, out var region))
            {
                mismatches.Add($"'{block.Name}' (README.md line {block.Line}): no `// begin-snippet: {block.Name}` region " +
                    $"in samples/{SnippetProjectFolder}.");
                continue;
            }

            var expected = Normalize(region.Code);
            var actual = Normalize(block.Code);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                mismatches.Add($"'{block.Name}' (README.md line {block.Line} vs {region.File}): {FirstDifference(expected, actual)}");
        }

        Assert.True(mismatches.Count == 0,
            "README.md C# blocks have drifted from the compiled snippets. Change the region in " +
            $"samples/{SnippetProjectFolder} first (so it compiles), then copy it into README.md:\n  " +
            string.Join("\n  ", mismatches));
    }

    [Fact]
    public void SnippetNames_AreUnique_AndEveryRegionIsShownInTheReadme()
    {
        var (blocks, _) = ParseReadme();
        var (regions, regionProblems) = ParseSampleRegions();
        Assert.True(regionProblems.Count == 0, string.Join("\n", regionProblems));

        var readmeNames = blocks.Where(b => b.Name is not null).Select(b => b.Name!).ToList();
        var duplicates = readmeNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "README.md uses a snippet name more than once: " + string.Join(", ", duplicates));

        var unused = regions.Keys.Except(readmeNames, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0,
            $"samples/{SnippetProjectFolder} has regions that README.md does not show (delete them or add the README block): " +
            string.Join(", ", unused));
    }

    [Fact]
    public void SnippetProject_IsBuiltWithTheSolution()
    {
        // The compile half of this gate only exists while the solution builds the project: drop it from
        // AgentEval.sln and a README block could name a missing API again with every test still green.
        var solution = File.ReadAllText(Path.Combine(RepoRoot(), "AgentEval.sln"));
        Assert.Contains(
            $"\"samples\\{SnippetProjectFolder}\\{SnippetProjectFolder}.csproj\"",
            solution,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_IgnoresLineEndingsIndentationAndTrailingWhitespace_ButNotContent()
    {
        Assert.Equal(Normalize("a();\n  b();\n"), Normalize("\r\n        a();  \r\n          b();\r\n\r\n"));
        Assert.NotEqual(Normalize("a();\nb();"), Normalize("a();\n b();"));
        Assert.NotEqual(Normalize("a(1);"), Normalize("a(2);"));
    }

    private sealed record CodeBlock(string? Name, int Line, string Code);

    private sealed record Region(string File, string Code);

    private static (List<CodeBlock> Blocks, List<string> Problems) ParseReadme()
    {
        var lines = SplitLines(File.ReadAllText(Path.Combine(RepoRoot(), "README.md")));
        var blocks = new List<CodeBlock>();
        var problems = new List<string>();
        string? pendingMarker = null;
        var pendingMarkerLine = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var info = trimmed[3..].Trim();
                var isCSharp = info.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                    || info.Equals("cs", StringComparison.OrdinalIgnoreCase)
                    || info.Equals("c#", StringComparison.OrdinalIgnoreCase);
                var fenceLine = i + 1;

                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    body.Add(lines[i++]);
                if (i >= lines.Length)
                    problems.Add($"line {fenceLine}: code fence is never closed.");

                if (isCSharp)
                {
                    if (pendingMarker is null)
                        problems.Add($"line {fenceLine}: ```{info} block has no `<!-- snippet: <name> -->` marker on the line before it.");
                    blocks.Add(new CodeBlock(pendingMarker, fenceLine, string.Join('\n', body)));
                }
                else if (pendingMarker is not null)
                {
                    problems.Add($"line {pendingMarkerLine}: marker '{pendingMarker}' is followed by a ```{info} block, not a C# block.");
                }

                pendingMarker = null;
                continue;
            }

            var marker = s_markerRegex.Match(trimmed);
            if (marker.Success)
            {
                if (pendingMarker is not null)
                    problems.Add($"line {pendingMarkerLine}: marker '{pendingMarker}' is not followed by a C# block.");
                pendingMarker = marker.Groups["name"].Value;
                pendingMarkerLine = i + 1;
                continue;
            }

            if (trimmed.Length > 0 && pendingMarker is not null)
            {
                problems.Add($"line {pendingMarkerLine}: marker '{pendingMarker}' is not immediately followed by a C# block.");
                pendingMarker = null;
            }
        }

        if (pendingMarker is not null)
            problems.Add($"line {pendingMarkerLine}: marker '{pendingMarker}' is not followed by a C# block.");

        return (blocks, problems);
    }

    private static (Dictionary<string, Region> Regions, List<string> Problems) ParseSampleRegions()
    {
        var root = RepoRoot();
        var projectDir = Path.Combine(root, "samples", SnippetProjectFolder);
        Assert.True(Directory.Exists(projectDir), $"Snippet project not found at {projectDir}.");

        var regions = new Dictionary<string, Region>(StringComparer.Ordinal);
        var problems = new List<string>();
        var sources = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDir, path)))
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in sources)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = SplitLines(File.ReadAllText(path));
            string? open = null;
            var openLine = 0;
            var body = new List<string>();

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var begin = s_beginRegex.Match(trimmed);
                if (begin.Success)
                {
                    if (open is not null)
                        problems.Add($"{relative}:{i + 1}: region '{begin.Groups["name"].Value}' starts inside region '{open}'.");
                    open = begin.Groups["name"].Value;
                    openLine = i + 1;
                    body.Clear();
                    continue;
                }

                if (s_endRegex.IsMatch(trimmed))
                {
                    if (open is null)
                    {
                        problems.Add($"{relative}:{i + 1}: `// end-snippet` without a matching begin.");
                        continue;
                    }

                    if (!regions.TryAdd(open, new Region($"{relative}:{openLine}", string.Join('\n', body))))
                        problems.Add($"{relative}:{openLine}: region '{open}' is defined more than once.");
                    open = null;
                    continue;
                }

                if (open is not null)
                    body.Add(lines[i]);
            }

            if (open is not null)
                problems.Add($"{relative}:{openLine}: region '{open}' has no `// end-snippet`.");
        }

        return (regions, problems);
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Line endings, trailing whitespace, leading/trailing blank lines and the indentation common to every
    /// non-blank line are presentation, not code: the region sits inside a method, the README block at column 0.
    /// Everything else, including relative indentation, must match.
    /// </summary>
    private static string Normalize(string code)
    {
        var lines = SplitLines(code).Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return string.Empty;

        var indentation = lines.Where(line => line.Length > 0).Min(line => line.Length - line.TrimStart().Length);
        return string.Join('\n', lines.Select(line => line.Length >= indentation ? line[indentation..] : line));
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<end of region>";
            var a = i < actualLines.Length ? actualLines[i] : "<end of README block>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
                return $"snippet line {i + 1}: region has `{e}`, README has `{a}`.";
        }

        return "they differ.";
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
