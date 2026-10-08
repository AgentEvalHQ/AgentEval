// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Results.Runs;

/// <summary>
/// The paths of a run folder ([RUN-3], contracts/aef/1/spec/03-run.md): segments of ASCII letters, digits, <c>.</c>,
/// <c>_</c> and <c>-</c> separated by <c>/</c>, at most 255 bytes in all; no segment empty, starting or ending with
/// <c>.</c>, or (ignoring case and everything from its first <c>.</c>) a name Windows reserves; and no two paths, and
/// no two of their folders, that differ only in letter case. So no file system re-encodes, re-orders or merges them.
/// </summary>
public static class AefPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Whether one path keeps [RUN-3] on its own (a case clash takes the other paths: <see cref="Check"/>).</summary>
    public static bool IsValid(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Encoding.UTF8.GetByteCount(path) > AefLimits.MaxPathBytes)
        {
            return false;   // a path problem, not a limit
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment[^1] == '.'
                || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }

            // CON, con.txt, Aux.tar.gz: the name before the first '.', in any case.
            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            if (Reserved.Contains(dot < 0 ? segment : segment[..dot]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The <c>path</c> problems of [RUN-3] over the paths of one run folder, in the order of §3.9, each path at most
    /// once: every path that breaks it on its own, and, for two paths that (or two of whose folders) differ only in
    /// letter case, the later of the two in byte order.
    /// </summary>
    public static IReadOnlyList<AefProblem> Check(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var distinct = paths.Distinct(StringComparer.Ordinal).ToList();
        var bad = new HashSet<string>(distinct.Where(p => !IsValid(p)), StringComparer.Ordinal);

        // At each depth, the first in byte order of the prefixes (folders, or whole paths) that fold to the same ASCII
        // lower case. A path whose prefix at some depth is not that first one clashes with a path before it.
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in distinct)
        {
            foreach (var prefix in Prefixes(path))
            {
                var folded = Fold(prefix);
                if (!first.TryGetValue(folded, out var earliest) || AefProblemOrder.CompareUtf8(prefix, earliest) < 0)
                {
                    first[folded] = prefix;
                }
            }
        }

        foreach (var path in distinct)
        {
            if (Prefixes(path).Any(prefix => !string.Equals(first[Fold(prefix)], prefix, StringComparison.Ordinal)))
            {
                bad.Add(path);
            }
        }

        return AefProblemOrder.Sort(bad.Select(p => new AefProblem(p, "path")));
    }

    // "ext/Data/x" → "ext", "ext/Data", "ext/Data/x". Folding keeps the number of segments, so prefixes that fold alike
    // are at the same depth.
    private static IEnumerable<string> Prefixes(string path)
    {
        for (var i = path.IndexOf('/', StringComparison.Ordinal); i >= 0; i = path.IndexOf('/', i + 1))
        {
            yield return path[..i];
        }

        yield return path;
    }

    private static string Fold(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] | 0x20) : source[i];
        }
    });
}
