// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.IO;
using System.Text.RegularExpressions;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Docs;

/// <summary>
/// Drift-detector for <c>docs/benchmarks/agentic/cost-guidance.md</c>. Every row
/// in the per-evaluator cost table must agree with the cost tier registered in
/// <see cref="EvaluatorCostMap"/>. Pass-1 + pass-2 + pass-3 reviews all caught
/// at least one tier-drift between the doc and the code; this test exists so
/// the next drift fails CI instead of getting flagged by another Opus pass.
/// </summary>
public class CostGuidanceLockdownTests
{
    // Match rows of the form `| evaluator_key | TIER | …notes…` where TIER is one
    // of the four shipped values. Header / separator / preset-summary rows fall
    // through naturally because their second column isn't a tier literal.
    private static readonly Regex s_rowRegex = new(
        @"^\|\s*`?(?<key>\w+)`?\s*\|\s*(?<tier>TRIVIAL|LOW|MEDIUM|HIGH)\s*\|",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void CostGuidanceTable_RowsAgreeWithEvaluatorCostMap()
    {
        var docPath = LocateCostGuidanceDoc();
        Assert.True(File.Exists(docPath),
            $"cost-guidance.md not found at {docPath}. " +
            "If the file moved, update the LocateCostGuidanceDoc() probe.");

        var doc = File.ReadAllText(docPath);
        var matches = s_rowRegex.Matches(doc);
        Assert.NotEmpty(matches);

        var mismatches = new System.Collections.Generic.List<string>();
        foreach (Match m in matches)
        {
            var key = m.Groups["key"].Value;

            // Skip rows whose first column isn't a real evaluator key — preset
            // names, header words, etc. fall through here. We only assert on
            // keys that EvaluatorCostMap recognises.
            if (!EvaluatorCostMap.IsRegistered(key)) continue;

            var docTierStr = m.Groups["tier"].Value;
            // EvaluatorCostTier enum members use PascalCase ("Trivial", "Low",
            // "Medium", "High"); doc uses uppercase. Case-insensitive parse.
            var docTier = System.Enum.Parse<EvaluatorCostTier>(docTierStr, ignoreCase: true);
            var mapTier = EvaluatorCostMap.GetTier(key);

            if (docTier != mapTier)
                mismatches.Add($"`{key}`: doc says {docTier.ToString().ToUpperInvariant()}, EvaluatorCostMap says {mapTier.ToString().ToUpperInvariant()}");
        }

        Assert.True(
            mismatches.Count == 0,
            "cost-guidance.md has drifted from EvaluatorCostMap. Update the doc rows OR the cost map — but they must agree. Mismatches:\n  " +
            string.Join("\n  ", mismatches));
    }

    private static string LocateCostGuidanceDoc()
    {
        // Walk up from the test bin dir until we find the docs/ folder. Test
        // execution can be from bin/Debug/netX.Y or bin/Release/netX.Y.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "benchmarks", "agentic", "cost-guidance.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback to a workspace-relative path — useful when the test runs
        // from a CI mount whose working dir is the repo root.
        return Path.Combine("docs", "benchmarks", "agentic", "cost-guidance.md");
    }
}
