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
            if (!string.IsNullOrEmpty(result.Error) || result.Items.Count == 0)
            {
                // Neutral infra branch (timeout / exception / breaker-open / empty result set). Do NOT bridge:
                // the bridge yields a fail/high composite (an empty composite is "not passed") that would
                // sink the whole report. Render it neutral instead.
                var reason = !string.IsNullOrEmpty(result.Error) ? result.Error : "no results returned";
                // Use result.Status as the primary discriminant (reliable, set by both TracingAgentEvaluator
                // and HybridEvalInterop.SkippedResults). Fall back to the ProviderName suffix for results
                // produced by older code paths that may not set Status.
                var neutralLabel = string.Equals(result.Status, "skipped", StringComparison.OrdinalIgnoreCase)
                    || result.ProviderName.EndsWith("(skipped)", StringComparison.Ordinal)
                    ? "skipped" : "error";
                branch = NeutralBranch($"hybrid.{Sanitize(source)}", source, neutralLabel, reason);
            }
            else if (composite is not null && IsLocalAgentEval(source) && composite.CapturedResults.Count > 0 && result.Items.Count > 0)
            {
                // Rich branch: the composite's full weighted hierarchy (one tree per query). CapturedResults
                // ACCUMULATES across calls and is never cleared, so splice only the most recent Items.Count
                // trees — otherwise a reused composite instance would leak prior runs' trees into this report.
                var recent = composite.CapturedResults.TakeLast(result.Items.Count).ToList();
                branch = Node($"hybrid.{Sanitize(source)}", source, "agentic", recent, source);
            }
            else
            {
                // Flat branch: bridge the MAF results into an EvalResult tree, then flatten redundant
                // wrapper levels so the report stays shallow and readable.
                //
                // MeaiToEvalResultBridge.Build produces:
                //   maf.eval (source)
                //     └─ maf.eval.query0  (query text)
                //          └─ metric leaf …
                //
                // We want instead (single query → promote metrics directly; multi-query → keep per-query):
                //   hybrid.<source>
                //     └─ metric leaf …          (single query)
                //  OR
                //   hybrid.<source>
                //     └─ maf.eval.query0 …      (multiple queries)
                //          └─ metric leaf …
                var bridged = MeaiToEvalResultBridge.Build(source, queries, result, judgeModel: null);
                var querySubs = bridged.Details.SubResults ?? [];
                IReadOnlyList<EvalResult> hybridChildren = querySubs.Count == 1
                    && querySubs[0].Details.SubResults is { Count: > 0 } leafSubs
                        ? leafSubs          // single query → expose metric leaves directly
                        : querySubs;        // multiple queries → keep per-query level
                branch = Node($"hybrid.{Sanitize(source)}", source, "agentic", hybridChildren, source);
            }

            // Attach the provider's portal link (when present) as evidence on the branch. Use the actual
            // source label (not a hard-coded "foundry") so a non-Foundry provider isn't misattributed, and
            // APPEND so it never clobbers evidence the branch already carries (e.g. a neutral error branch).
            if (result.ReportUrl is not null)
            {
                var link = new EvalEvidence(source, "report_url", result.ReportUrl.ToString());
                var evidence = branch.Details.Evidence is { } existing ? existing.Append(link).ToArray() : new[] { link };
                branch = branch with { Details = branch.Details with { Evidence = evidence } };
            }

            branches.Add(branch);
        }

        return Node("hybrid.eval", title, "agentic", branches, "composite");
    }

    private static bool IsLocalAgentEval(string source) =>
        source.Contains("local", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("agenteval", StringComparison.OrdinalIgnoreCase);

    // Mean-aggregated node tagged with `provenanceType`, read by measurement state (MeasuredRollup): an errored or
    // skipped part is never averaged in as a 0 and never lets the node pass on the rest, and a warn stays a warn. It
    // left "error"/"skipped" parts out and passed on whatever remained — an MEAI metric whose judge reply did not parse
    // beside a passing one read PASS — and read any other non-pass, a quality WARN included, as FAIL/high (#203 review
    // round 5, B10u). An errored part is "error" unless a measured failure decides; nothing measured is no verdict.
    private static EvalResult Node(string key, string name, string category,
        IReadOnlyList<EvalResult> subs, string provenanceType)
    {
        var v = MeasuredRollup.Of(subs);
        return new EvalResult(
            Metric: new EvalMetadata(key, name, category, "1.0.0"),
            Score: new EvalScore(v.Value, null, v.Label, v.Passed, 0.70, v.Severity, null) { Measurement = v.Measurement },
            Details: new EvalDetails(null, null, null, subs, "mean"),
            Provenance: new EvalProvenance(provenanceType, null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // A neutral infra branch (label "error"/"skipped", severity none) — visible but never a real failure.
    private static EvalResult NeutralBranch(string key, string name, string label, string reason) => new(
        Metric: new EvalMetadata(key, name, "agentic", "1.0.0"),
        Score: new EvalScore(0.0, null, label, false, 0.70, "none", null),
        Details: new EvalDetails(null, new[] { new EvalEvidence(name, label, reason) }, null, null, null),
        Provenance: new EvalProvenance(label, null, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UtcNow);

    private static string Sanitize(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
