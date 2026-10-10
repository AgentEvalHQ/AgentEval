// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Results;

/// <summary>
/// One problem a verifier reports (contracts/aef/1/spec/03-run.md, §3.9): where it is (a path in a run folder,
/// <c>&lt;file&gt;:&lt;line&gt;</c> for one NDJSON line, or the location an operation names, such as <c>event:3</c>)
/// and its code (<c>encoding</c>, <c>schema</c>, <c>path</c>, …). Problem lists compare as ordered lists of
/// [path, code] pairs ([CONF-2]), so they are kept in <see cref="AefProblemOrder"/>.
/// </summary>
public readonly record struct AefProblem(string Path, string Code);

/// <summary>
/// The order of §3.9: by path, then by code. Paths compare by their UTF-8 bytes, except that two line paths of one
/// NDJSON file compare by line number as a number (<c>results.ndjson:9</c> before <c>results.ndjson:10</c>). A line
/// path is <c>&lt;file&gt;:&lt;digits&gt;</c> whose file ends in <c>.ndjson</c> or <c>.jsonl</c>, the NDJSON files of
/// [RUN-2]; any other path with a colon (<c>run:10</c> of [STRM-4]) compares by its bytes. Codes compare by their bytes.
/// </summary>
public sealed class AefProblemOrder : IComparer<AefProblem>
{
    /// <summary>The order.</summary>
    public static readonly AefProblemOrder Instance = new();

    /// <summary>Paths in the order of §3.9.</summary>
    public static IComparer<string> Paths { get; } = Comparer<string>.Create(ComparePaths);

    /// <summary>Strings by their UTF-8 bytes (equivalently, by their code points), the order AEF sorts names in.</summary>
    public static IComparer<string> Utf8 { get; } = Comparer<string>.Create(CompareUtf8);

    private AefProblemOrder()
    {
    }

    /// <inheritdoc />
    public int Compare(AefProblem x, AefProblem y)
    {
        var byPath = ComparePaths(x.Path, y.Path);
        return byPath != 0 ? byPath : CompareUtf8(x.Code, y.Code);
    }

    /// <summary>The problems in the order of §3.9 (a stable sort: equal problems keep their order).</summary>
    public static IReadOnlyList<AefProblem> Sort(IEnumerable<AefProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return [.. problems.Order(Instance)];
    }

    /// <summary>Compares two paths as §3.9 orders them.</summary>
    public static int ComparePaths(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        if (LinePath(a) is var (fileA, lineA) && LinePath(b) is var (fileB, lineB)
            && fileA.Equals(fileB, StringComparison.Ordinal))
        {
            // Line numbers have no leading zeros: the longer is the larger, and equal lengths compare digit by digit.
            return lineA.Length != lineB.Length ? lineA.Length.CompareTo(lineB.Length) : string.CompareOrdinal(lineA, lineB);
        }

        return CompareUtf8(a, b);
    }

    /// <summary>Compares two strings by their UTF-8 bytes, which is the order of their code points.</summary>
    public static int CompareUtf8(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            // A lone surrogate decodes as U+FFFD, as UTF-8 encoding would write it.
            Rune.DecodeFromUtf16(a.AsSpan(i), out var ra, out var na);
            Rune.DecodeFromUtf16(b.AsSpan(j), out var rb, out var nb);
            if (ra != rb)
            {
                return ra.Value.CompareTo(rb.Value);
            }

            i += na;
            j += nb;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    // (file, line digits) of a line path, or null.
    private static (string File, string Line)? LinePath(string path)
    {
        var colon = path.LastIndexOf(':');
        if (colon <= 0 || colon == path.Length - 1)
        {
            return null;
        }

        var file = path[..colon];
        var line = path[(colon + 1)..];
        return line.All(char.IsAsciiDigit)
               && (file.EndsWith(".ndjson", StringComparison.Ordinal) || file.EndsWith(".jsonl", StringComparison.Ordinal))
            ? (file, line.TrimStart('0') is { Length: > 0 } digits ? digits : "0")
            : null;
    }
}
