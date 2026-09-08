// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Repository;

/// <summary>
/// No C# source file carries a UTF-8 byte-order mark.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This exists because I introduced the same defect twice in one change set.</b> Editing
/// tooling that reads with <c>utf-8-sig</c> and writes with <c>utf-8-sig</c> adds a BOM whether or
/// not the file had one. The first pass put BOMs into 22 files; after stripping all 22, the very
/// next commit reintroduced 3. Both times a reviewer found it, not me, because a BOM is invisible in
/// a diff and changes nothing a compiler complains about.
/// </para>
/// <para>
/// The convention is not a preference — it was measured. At the time this test was written,
/// <b>2231 of 2252</b> tracked <c>.cs</c> files had no BOM, and every one of the 21 exceptions came
/// from a single branch. So the rule is: none.
/// </para>
/// <para>
/// ⚠ <b>Scoped to <c>.cs</c> deliberately.</b> A number of Markdown, <c>.sln</c> and <c>docs/</c>
/// files DO carry BOMs and have since long before this test; widening it would turn a guard into a
/// 30-file reformatting demand nobody asked for. If those are ever normalised, widen this then — and
/// note that stripping a BOM changes a file's bytes, so anything with a pinned hash must be checked
/// first.
/// </para>
/// </remarks>
public class NoByteOrderMarkInNewSourceTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void NoCSharpSourceFileStartsWithAByteOrderMark()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var sources = Directory
            .EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(f))
            .ToList();

        // ⚠ Positive control FIRST. "No file has a BOM" is also true of a scan that found no files —
        // the shape this repository has recorded six times, and the reason every census here states
        // its denominator before its finding.
        Assert.True(sources.Count > 1500,
            $"the scan found only {sources.Count} .cs files, so it measured nothing");

        var offenders = sources.Where(StartsWithBom).Select(f => Relative(root!, f)).ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} C# file(s) start with a UTF-8 BOM, against a repository convention of "
            + $"none in {sources.Count}. A BOM is invisible in a diff and breaks scanners that expect "
            + $"the SPDX identifier at byte 0:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", offenders.Take(20)));
    }

    private static bool StartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && head.SequenceEqual(Utf8Bom);
    }

    /// <summary>Build output is not source, and it is not ours to normalise.</summary>
    private static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
