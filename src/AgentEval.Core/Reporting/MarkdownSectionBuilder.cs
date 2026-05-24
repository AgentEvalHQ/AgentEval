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
/// <see cref="StringBuilder"/> followed by a blank line. Helpers are no-ops when
/// their inputs lack the required data (e.g. no pillars, no manifest hash) so
/// they can be called unconditionally.
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
    /// Renders a <c>## Audit Chain</c> section covering the manifest hash, the source-run
    /// reference, and an inferred chain-valid indicator.
    /// <para>
    /// Chain validity is inferred from presence of a non-empty <see cref="SourceRunRef.ManifestHash"/>:
    /// the manifest hash is the anchor every downstream verification step uses; absence indicates
    /// the evidence cannot be re-verified against its source run.
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

        var hashShown = string.IsNullOrWhiteSpace(sourceRun.ManifestHash)
            ? "—"
            : sourceRun.ManifestHash;
        sb.AppendLine($"**Manifest hash**: `{hashShown}`  ");

        if (!string.IsNullOrWhiteSpace(previousEvidenceRef))
            sb.AppendLine($"**Previous evidence**: `{previousEvidenceRef}`  ");
        else
            sb.AppendLine("**Previous evidence**: —  ");

        var chainValid = !string.IsNullOrWhiteSpace(sourceRun.ManifestHash);
        var indicator = chainValid ? "VALID" : "BROKEN";
        sb.AppendLine($"**Chain status**: **{indicator}**");
        sb.AppendLine();
    }
}
