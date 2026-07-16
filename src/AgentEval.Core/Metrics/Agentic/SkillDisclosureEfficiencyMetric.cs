// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Skills;

namespace AgentEval.Metrics.Agentic;

/// <summary>
/// Options for <see cref="SkillDisclosureEfficiencyMetric"/>.
/// </summary>
public sealed class SkillDisclosureEfficiencyOptions
{
    /// <summary>
    /// The minimum number of DISTINCT skills loaded, with ZERO subsequent
    /// <c>read_skill_resource</c>/<c>run_skill_script</c> follow-through, to flag a "load storm" — the
    /// anti-pattern progressive disclosure exists to prevent (loading many skills "just in case" and
    /// never using any of them). Default 3.
    /// </summary>
    public int LoadStormThreshold { get; init; } = 3;

    /// <summary>
    /// Multiplicative penalty (0..1) applied to the load-precision component when a load storm is
    /// detected. Default 0.7 (a 30% cut).
    /// </summary>
    public double LoadStormPenaltyFactor { get; init; } = 0.7;
}

/// <summary>
/// Structural, code-based (free — no LLM call) efficiency metric for MAF Agent Skills' progressive
/// disclosure funnel (<c>load_skill</c> → <c>read_skill_resource</c> → <c>run_skill_script</c>).
/// Scores three components as a weighted PRODUCT (each normalized 0..1, multiplied, then ×100):
/// <list type="number">
///   <item><b>Disclosure-order validity</b> — the fraction of read/run calls properly preceded by a
///   same-skill <c>load_skill</c> (see <see cref="AgentEval.Skills"/>'s shared analyzer). No reads/runs
///   at all is trivially valid (1.0).</item>
///   <item><b>Load precision</b> — penalizes redundant loads (the same skill loaded ≥2×) via a
///   uniqueness ratio, and "load storms" (many skills loaded, none subsequently read/run) via
///   <see cref="SkillDisclosureEfficiencyOptions"/>.</item>
///   <item><b>Selection F1 (optional)</b> — ONLY when <c>context.Properties["expected_skills"]</c>
///   (an <see cref="IReadOnlyList{T}"/> of <see cref="string"/>) is supplied: F1 of the skills actually
///   loaded vs. the expected set. Absent ground truth this factor is a neutral 1.0 (never a fabricated
///   "100% selection accuracy") and <see cref="MetricResult.Explanation"/> says verbatim that the score
///   is structural-only.</item>
/// </list>
/// </summary>
/// <remarks>
/// The fourth conceptual progressive-disclosure stage — "advertise" (the skill inventory listed into
/// the system prompt via the <c>{skills}</c> placeholder) — is NOT a tool call, so it is never
/// observable from a <see cref="Models.ToolUsageReport"/> and this metric never fabricates an advertise
/// count. <see cref="MetricResult.Details"/> carries exactly the three OBSERVABLE stage counts plus the
/// funnel label <c>"L→R→S"</c>.
/// </remarks>
public sealed class SkillDisclosureEfficiencyMetric : IAgenticMetric
{
    private readonly SkillDisclosureEfficiencyOptions _options;

    /// <inheritdoc/>
    public string Name => "code_skill_disclosure_efficiency";

    /// <inheritdoc/>
    public string Description =>
        "Structural efficiency of progressive skill disclosure (load→read→run funnel); optional load-selection F1 when expected_skills is supplied.";

    /// <inheritdoc/>
    public MetricCategory Categories => MetricCategory.Agentic | MetricCategory.CodeBased | MetricCategory.RequiresToolUsage;

    /// <inheritdoc/>
    public bool RequiresToolUsage => true;

    /// <summary>Create a skill disclosure efficiency metric.</summary>
    /// <param name="options">Tuning for load-storm detection. Defaults when omitted.</param>
    public SkillDisclosureEfficiencyMetric(SkillDisclosureEfficiencyOptions? options = null)
    {
        _options = options ?? new SkillDisclosureEfficiencyOptions();
    }

    /// <inheritdoc/>
    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var analysis = SkillDisclosureAnalyzer.Analyze(context.ToolUsage);

        if (analysis.LoadCount == 0 && analysis.ReadCount == 0 && analysis.RunCount == 0)
        {
            return Task.FromResult(MetricResult.Pass(Name, 100,
                "No skill tools (load_skill / read_skill_resource / run_skill_script) were called — nothing to measure.",
                new Dictionary<string, object>
                {
                    ["nLoad"] = 0,
                    ["nRead"] = 0,
                    ["nRun"] = 0,
                    ["redundantLoads"] = 0,
                    ["orphanedReads"] = 0,
                    ["orphanedRuns"] = 0,
                    ["funnel"] = "L→R→S",
                }));
        }

        // 1. Disclosure-order validity.
        var followThrough = analysis.ReadCount + analysis.RunCount;
        var orderValidity = followThrough == 0
            ? 1.0
            : 1.0 - (double)analysis.OrphanedCount / followThrough;

        // 2. Load precision: redundancy ratio × load-storm penalty.
        var redundancyFactor = analysis.LoadCount == 0
            ? 1.0
            : (double)analysis.UniqueSkillsLoaded / analysis.LoadCount;

        var isLoadStorm = analysis.UniqueSkillsLoaded >= _options.LoadStormThreshold && followThrough == 0;
        var loadStormFactor = isLoadStorm ? _options.LoadStormPenaltyFactor : 1.0;
        var loadPrecision = Math.Clamp(redundancyFactor * loadStormFactor, 0.0, 1.0);

        // 3. Optional selection F1 (only with ground truth).
        var expectedSkills = context.GetProperty<IReadOnlyList<string>>("expected_skills");
        var hasGroundTruth = expectedSkills is { Count: > 0 };
        double selectionFactor = 1.0;
        double? selectionPrecision = null, selectionRecall = null, selectionF1 = null;

        if (hasGroundTruth)
        {
            var loadedNames = context.ToolUsage!.GetCallsByName(SkillToolNames.LoadSkill)
                .Select(c => SkillArgumentExtraction.ExtractSkillName(c.Arguments))
                .Where(n => n is not null)
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedSet = expectedSkills!.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var truePositives = loadedNames.Count(expectedSet.Contains);
            var precision = loadedNames.Count == 0 ? 0.0 : (double)truePositives / loadedNames.Count;
            var recall = expectedSet.Count == 0 ? 1.0 : (double)truePositives / expectedSet.Count;
            var f1 = precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);

            selectionPrecision = precision;
            selectionRecall = recall;
            selectionF1 = f1;
            selectionFactor = f1;
        }

        var score = Math.Clamp(orderValidity * loadPrecision * selectionFactor * 100.0, 0.0, 100.0);

        var details = new Dictionary<string, object>
        {
            ["nLoad"] = analysis.LoadCount,
            ["nRead"] = analysis.ReadCount,
            ["nRun"] = analysis.RunCount,
            ["redundantLoads"] = analysis.RedundantLoads,
            ["orphanedReads"] = analysis.OrphanedReads.Count,
            ["orphanedRuns"] = analysis.OrphanedRuns.Count,
            ["funnel"] = "L→R→S",
            ["orderValidity"] = orderValidity,
            ["loadPrecision"] = loadPrecision,
            ["loadStormDetected"] = isLoadStorm,
        };

        string explanation;
        if (hasGroundTruth)
        {
            details["selectionPrecision"] = selectionPrecision!.Value;
            details["selectionRecall"] = selectionRecall!.Value;
            details["selectionF1"] = selectionF1!.Value;
            explanation =
                $"Funnel L={analysis.LoadCount}→R={analysis.ReadCount}→S={analysis.RunCount}. " +
                $"Order validity {orderValidity:P0}, load precision {loadPrecision:P0}, selection F1 {selectionF1:P0} (vs expected_skills).";
        }
        else
        {
            explanation =
                $"Funnel L={analysis.LoadCount}→R={analysis.ReadCount}→S={analysis.RunCount}. " +
                $"Order validity {orderValidity:P0}, load precision {loadPrecision:P0}. " +
                "Structural only — no expected_skills supplied, so no selection score is computed.";
        }

        var passed = score >= EvaluationDefaults.PassingScoreThreshold;
        return Task.FromResult(passed
            ? MetricResult.Pass(Name, score, explanation, details)
            : MetricResult.Fail(Name, explanation, score, details));
    }
}
