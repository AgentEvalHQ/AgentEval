// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using Microsoft.Agents.AI;
using AgentEval.Evals;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Merges the per-source results of a hybrid eval (Foundry ⊕ AgentEval-local) into one source-tagged
/// <see cref="EvalResult"/> tree for rendering (HTML / PDF / <c>.agenteval</c>). One branch per source.
/// </summary>
/// <remarks>
/// Feed it either the per-source list captured by <see cref="CompositeAgentEvaluator.CapturedPerSource"/>,
/// or (for MAF's native <c>agent.EvaluateAsync(queries, IAgentEvaluator[])</c>) the returned results zipped
/// with their source labels. When the optional <c>composite</c> is supplied, the AgentEval-local
/// branch is spliced from its full weighted hierarchy (<see cref="AgentEvalCompositeEvaluator.CapturedResults"/>);
/// otherwise every branch is bridged flat via <see cref="MeaiToEvalResultBridge"/>.
/// </remarks>
public static class UnifiedEvalReport
{
    /// <summary>Builds the unified, source-tagged report tree.</summary>
    public static EvalResult Build(
        IReadOnlyList<(string Source, AgentEvaluationResults Result)> perSource,
        AgentEvalCompositeEvaluator? composite = null,
        string title = "Foundry ⊕ AgentEval")
    {
        ArgumentNullException.ThrowIfNull(perSource);
        var branches = new List<EvalResult>(perSource.Count);

        foreach (var (source, result) in perSource)
        {
            // Recover queries from the shared input items (same agent run for every source).
            var queries = result.InputItems is { Count: > 0 }
                ? result.InputItems.Select((it, i) => string.IsNullOrEmpty(it.Query) ? $"query[{i}]" : it.Query).ToList()
                : Enumerable.Range(0, result.Items.Count).Select(i => $"query[{i}]").ToList();

            EvalResult branch;
            if (composite is not null && IsLocalAgentEval(source) && composite.CapturedResults.Count > 0 && result.Items.Count > 0)
            {
                // Rich branch: the composite's full weighted hierarchy (one tree per query). CapturedResults
                // ACCUMULATES across calls and is never cleared, so splice only the most recent Items.Count
                // trees — otherwise a reused composite instance would leak prior runs' trees into this report.
                var recent = composite.CapturedResults.TakeLast(result.Items.Count).ToList();
                branch = Node($"hybrid.{Sanitize(source)}", source, "agentic", recent, source);
            }
            else
            {
                // Flat branch: reuse the bridge (per-query -> per-metric leaves), re-tagged as this source.
                var bridged = MeaiToEvalResultBridge.Build(source, queries, result, judgeModel: null);
                branch = Node($"hybrid.{Sanitize(source)}", source, "agentic", new[] { bridged }, source);
            }

            // Attach the provider's portal link (when present) as evidence on the branch. Use the actual
            // source label (not a hard-coded "foundry") so a non-Foundry provider isn't misattributed.
            if (result.ReportUrl is not null)
            {
                var evidence = new[] { new EvalEvidence(source, "report_url", result.ReportUrl.ToString()) };
                branch = branch with { Details = branch.Details with { Evidence = evidence } };
            }

            branches.Add(branch);
        }

        return Node("hybrid.eval", title, "agentic", branches, "composite");
    }

    private static bool IsLocalAgentEval(string source) =>
        source.Contains("local", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("agenteval", StringComparison.OrdinalIgnoreCase);

    // Mean-aggregated node tagged with `provenanceType` (mirrors MeaiToEvalResultBridge.Composite).
    private static EvalResult Node(string key, string name, string category,
        IReadOnlyList<EvalResult> subs, string provenanceType)
    {
        var avg = subs.Count == 0 ? 0 : subs.Average(s => s.Score.Value);
        var passed = subs.Count > 0 && subs.All(s => s.Score.Passed);
        return new EvalResult(
            Metric: new EvalMetadata(key, name, category, "1.0.0"),
            Score: new EvalScore(avg, null, passed ? "pass" : "fail", passed, 0.70, passed ? "none" : "high", null),
            Details: new EvalDetails(null, null, null, subs, "mean"),
            Provenance: new EvalProvenance(provenanceType, null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static string Sanitize(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
