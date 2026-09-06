// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals.Meta;
using Microsoft.Extensions.AI.Evaluation;

// AgentEval.Core also has an EvaluationResult. Alias rather than fully-qualify at each site: the
// collision is the ADR's own point — six result models, and this file is the only bridge.
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.Evals;

/// <summary>
/// One-way projections from this library's result models — and from
/// <c>Microsoft.Extensions.AI.Evaluation</c>'s — onto the neutral
/// <see cref="Observation"/> tuple.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-030 §3.2 and Slice 2.2. One-way, permanently.</b> There is no
/// <c>Observation → EvalResult</c> here and there must never be one. A floor is a property OF a
/// comparison; a control is a RUN OF an eval; a comparison is a FUNCTION OF results. The moment one
/// of them can produce a result model, AgentEval has a seventh result model and it is the one
/// holding pass/fail authority — the worst possible place for a fork. The rule is enforced by
/// reflection in <c>ObservationAdapterTests</c>, not by this paragraph.
/// </para>
/// <para>
/// <b>These live in <c>AgentEval.Core</c>, not in the meta namespace.</b> They are the only place
/// the two worlds touch, and putting them on the AgentEval side is what keeps
/// <c>AgentEval.Evals.Meta</c> BCL-only and therefore portable.
/// </para>
/// </remarks>
public static class ObservationAdapters
{
    /// <summary>Projects an <see cref="EvalResult"/> onto an observation.</summary>
    /// <param name="result">The result to project.</param>
    /// <param name="caseId">Stable case identity — <c>EvalInput.CaseId</c>, not a display string.</param>
    /// <param name="armId">Which arm produced it.</param>
    /// <returns>The observation.</returns>
    /// <remarks>
    /// The state comes from <see cref="EvalScoreExtensions.CensusBucket"/>, which reads BOTH
    /// <c>Measurement</c> and <c>Label</c> — so a skipped or errored leaf arrives as
    /// <see cref="MeasurementState.NotMeasured"/> rather than as a zero, and an inapplicable one as
    /// <see cref="MeasurementState.NotApplicable"/>, whichever half of the pair carries the fact.
    /// </remarks>
    public static Observation ToObservation(this EvalResult result, string caseId, string armId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = result.Score.CensusBucket();

        return state == MeasurementState.Measured
            ? Observation.Measured(caseId, armId, result.Score.Value)
            : new Observation(caseId, armId, 0.0, state);
    }

    /// <summary>Projects a <see cref="MetricResult"/> onto an observation.</summary>
    /// <param name="result">The result to project.</param>
    /// <param name="caseId">Stable case identity.</param>
    /// <param name="armId">Which arm produced it.</param>
    /// <returns>The observation, with the score normalised to 0..1.</returns>
    /// <remarks>
    /// ⚠ <see cref="MetricResult.Score"/> is documented as <b>0 to 100</b> while
    /// <c>EvalScore.Value</c> is 0 to 1, and the meta lane compares them side by side. The division
    /// happens here, once, rather than at each call site — a comparison of a 0..100 arm against a
    /// 0..1 arm is a wins/losses table that means nothing, and it would look entirely plausible.
    /// A non-finite score becomes <see cref="MeasurementState.NotMeasured"/>: a metric that could
    /// not produce a number did not measure the case.
    /// </remarks>
    public static Observation ToObservation(this MetricResult result, string caseId, string armId)
    {
        ArgumentNullException.ThrowIfNull(result);

        return double.IsFinite(result.Score)
            ? Observation.Measured(caseId, armId, result.Score / 100.0)
            : Observation.NotMeasured(caseId, armId);
    }

    /// <summary>
    /// Projects a <c>Microsoft.Extensions.AI.Evaluation.EvaluationResult</c> onto an observation.
    /// </summary>
    /// <param name="result">The result to project.</param>
    /// <param name="caseId">Stable case identity.</param>
    /// <param name="armId">Which arm produced it.</param>
    /// <param name="metricName">
    /// Which metric to read. Null takes the first, which is what the shipped adapter does.
    /// </param>
    /// <returns>The observation.</returns>
    /// <remarks>
    /// <para>
    /// A <c>NumericMetric</c> uses M.E.AI's 1–5 scale, normalised here to 0..1. A metric present but
    /// carrying <b>no value</b> — M.E.AI's first-class "the judge could not speak", produced by an
    /// unparseable response, a content filter or an evaluator error — becomes
    /// <see cref="MeasurementState.NotMeasured"/>. <b>It is not a zero.</b> Scoring it as one is the
    /// defect this whole lane exists to prevent: an instrument that did not run reported as an arm
    /// that failed.
    /// </para>
    /// <para>
    /// An empty metric set is also <see cref="MeasurementState.NotMeasured"/>, for the same reason.
    /// </para>
    /// </remarks>
    public static Observation ToObservation(
        this MeaiEvaluationResult result, string caseId, string armId, string? metricName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var metrics = result.Metrics;
        if (metrics is null || metrics.Count == 0) return Observation.NotMeasured(caseId, armId);

        EvaluationMetric? metric = null;
        if (metricName is null)
        {
            foreach (var pair in metrics) { metric = pair.Value; break; }
        }
        else if (metrics.TryGetValue(metricName, out var named))
        {
            metric = named;
        }

        return metric switch
        {
            NumericMetric { Value: { } v } => Observation.Measured(caseId, armId, Math.Clamp((v - 1.0) / 4.0, 0.0, 1.0)),
            BooleanMetric { Value: { } b } => Observation.Measured(caseId, armId, b ? 1.0 : 0.0),
            _ => Observation.NotMeasured(caseId, armId),
        };
    }
}
