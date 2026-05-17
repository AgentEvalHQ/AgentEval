// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Compliance.EuAiAct.Reporting;

/// <summary>
/// Walks the composite result tree depth-first and returns all leaf
/// <see cref="EvalResult"/> nodes where <c>Score.Severity == "critical"</c>
/// and <c>!Score.Passed</c>, ordered by <c>Score.Value</c> ascending (worst first).
/// </summary>
public sealed class CriticalFindingExtractor
{
    /// <summary>
    /// Finds all critical failing leaf nodes in the result tree rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>
    /// A list of critical-severity failing leaf results, ordered by score ascending.
    /// </returns>
    public IReadOnlyList<EvalResult> Find(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var findings = new List<EvalResult>();
        WalkAtomicLeaves(root, findings);
        return findings
            .Where(f => f.Score.Severity == "critical" && !f.Score.Passed)
            .OrderBy(f => f.Score.Value)
            .ToList();
    }

    private static void WalkAtomicLeaves(EvalResult node, List<EvalResult> sink)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0) { sink.Add(node); return; }
        foreach (var s in subs) WalkAtomicLeaves(s, sink);
    }
}
