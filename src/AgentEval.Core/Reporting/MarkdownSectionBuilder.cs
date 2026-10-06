// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Core.Reporting;

/// <summary>
/// Shared Markdown section builders used by regulation-specific report renderers
/// (GDPR / EU AI Act) for parity. Phase-8 T2.5: methodology + audit-chain.
/// <para>
/// Each helper writes a self-contained <c>## Heading</c> block to the supplied
/// <see cref="StringBuilder"/> followed by a blank line. Helpers tolerate missing
/// data (e.g. no pillars, no manifest hash) and say so in the section instead of
/// throwing, so they can be called unconditionally.
/// </para>
/// </summary>
public static class MarkdownSectionBuilder
{
    /// <summary>
    /// Renders a <c>## Methodology</c> section covering pillar weights, the composite
    /// aggregation strategy, the judge model, and the judge mode.
    /// </summary>
    /// <param name="sb">Target string builder.</param>
    /// <param name="compositeTree">The composite tree whose top-level components are pillars.</param>
    /// <param name="evaluatorModel">Judge model identifier (e.g. <c>gpt-5-chat</c>).</param>
    /// <param name="judgeMode">Judge mode label (e.g. <c>mode-a</c>, <c>multi-judge</c>).</param>
    public static void AppendMethodology(
        StringBuilder sb,
        EvalResult compositeTree,
        string evaluatorModel,
        string judgeMode)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(compositeTree);

        sb.AppendLine("## Methodology");
        sb.AppendLine();
        sb.AppendLine($"**Judge model**: `{evaluatorModel ?? "unknown"}`  ");
        sb.AppendLine($"**Judge mode**: `{judgeMode ?? "unknown"}`  ");

        var aggStrategy = compositeTree.Details.AggregationStrategy ?? "unknown";
        sb.AppendLine($"**Top-level aggregation**: `{aggStrategy}`");
        sb.AppendLine();

        var pillars = compositeTree.Details.SubResults;
        if (pillars is { Count: > 0 })
        {
            // Weights are not on EvalResult — the composite tree only exposes sub-results
            // and the aggregation strategy. We surface the pillar keys so a reader can
            // cross-reference against the regulation's pillar-weight tables in docs.
            // Phase-8 follow-up: when ScoreNormalized or component weights are persisted
            // alongside EvalResult, render an explicit weight column here.
            sb.AppendLine("| Pillar | Score | Status |");
            sb.AppendLine("|---|---:|---|");
            foreach (var pillar in pillars)
            {
                var key = pillar.Metric.Key;
                var score = pillar.Score.Value;
                var status = pillar.Score.Passed ? "PASS" : (pillar.Score.Label ?? "—").ToUpperInvariant();
                sb.AppendLine($"| `{key}` | {score:P0} | **{status}** |");
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Renders a <c>## Audit Chain</c> section covering the source-run reference, the manifest
    /// hash, and what this report can say about the chain.
    /// <para>
    /// This helper does not verify the chain, so it never reports it as verified. It only receives
    /// the <see cref="SourceRunRef"/> copied into the evidence; checking that reference needs the
    /// source run's <c>manifest.json</c> and the run files its hash covers, which a report renderer
    /// does not have. A recorded hash is shown as recorded and not verified, followed by how to
    /// verify it (<c>agenteval doctor</c>). An empty hash is shown as no hash recorded.
    /// </para>
    /// <para>
    /// This section used to print <c>VALID</c> for any non-empty hash and <c>BROKEN</c> for an empty
    /// one. The first reported a check that never ran as passed; the second reported a missing anchor
    /// as a failed check.
    /// </para>
    /// </summary>
    /// <param name="sb">Target string builder.</param>
    /// <param name="sourceRun">Reference to the source run (run-id + manifest hash).</param>
    /// <param name="previousEvidenceRef">Optional pointer to a prior evidence document.</param>
    public static void AppendAuditChain(
        StringBuilder sb,
        SourceRunRef sourceRun,
        string? previousEvidenceRef = null)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(sourceRun);

        sb.AppendLine("## Audit Chain");
        sb.AppendLine();
        sb.AppendLine($"**Source run**: `{sourceRun.RunId}`  ");

        var hashRecorded = !string.IsNullOrWhiteSpace(sourceRun.ManifestHash);
        sb.AppendLine(hashRecorded
            ? $"**Manifest hash**: `{sourceRun.ManifestHash}`  "
            : "**Manifest hash**: —  ");

        if (!string.IsNullOrWhiteSpace(previousEvidenceRef))
            sb.AppendLine($"**Previous evidence**: `{previousEvidenceRef}`  ");
        else
            sb.AppendLine("**Previous evidence**: —  ");

        // Nothing here compares the hash with anything, so the status says what is on the page:
        // whether a hash was recorded. Only a real check may print a verified state.
        if (hashRecorded)
        {
            sb.AppendLine("**Chain status**: hash recorded, **not verified** in this report");
            sb.AppendLine();
            sb.AppendLine(
                "To verify, run `agenteval doctor` inside the solution whose `.agenteval/` workspace holds the " +
                "source run above. It re-hashes each run's files against that run's `manifest.json` and checks " +
                "the manifest hash in every compliance `evidence.json` against its source run. Then confirm the " +
                "hash above equals `contentHash` in that run's `manifest.json`.");
        }
        else
        {
            sb.AppendLine("**Chain status**: **no hash recorded**, so this evidence cannot be checked against its source run");
        }
        sb.AppendLine();
    }
}
