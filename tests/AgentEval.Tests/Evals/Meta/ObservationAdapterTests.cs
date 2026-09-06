// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;
using MeaiEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 Slice 2.2. The projections onto <see cref="Observation"/>, and the rule that they only
/// go one way.
/// </summary>
public class ObservationAdapterTests
{
    private static EvalResult ResultWith(EvalScore score) =>
        new(new("k", "n", "c", "1.0.0"), score, new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UnixEpoch);

    // ── The rule ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Adapters_AreOneWay()
    {
        // A floor is a property OF a comparison. A control is a RUN OF an eval. A comparison is a
        // FUNCTION OF results. The moment one of them can PRODUCE a result model, AgentEval has a
        // seventh result model and it is the one holding pass/fail authority.
        Type[] resultModels =
        [
            typeof(EvalResult), typeof(MetricResult), typeof(MeaiEvaluationResult),
        ];

        // ⚠ THE ASSEMBLY SET IS THE WHOLE TEST, and the first version of it named two types that
        // live in the SAME assembly. `Observation` and `EvalResult` are both in
        // AgentEval.Abstractions; `ObservationAdapters` — the only thing 2.2 is about — is in
        // AgentEval.Core, so the scan never reached a single adapter. Demonstrated by ablation: a
        // literal `public static EvalResult AblationBackToResult(Observation o)` added to
        // ObservationAdapters left this test GREEN. Anchor on the ADAPTER type, and on `Observation`
        // for the meta lane, and de-duplicate rather than assuming the two differ.
        var assemblies = new[] { typeof(Observation).Assembly, typeof(ObservationAdapters).Assembly }
            .Distinct()
            .ToList();

        var offenders = new List<string>();
        var scanned = 0;
        var forwardAdaptersSeen = 0;
        var adapterTypeEnumerated = false;

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // The POSITIVE CONTROL for reach: the adapters do not CONSUME an Observation (they
                // produce one), so the offender scan below can never touch them by construction.
                // What has to be proven instead is that the enumeration reached the adapter type at
                // all — which is exactly what the two-same-assembly bug prevented.
                if (type == typeof(ObservationAdapters))
                {
                    adapterTypeEnumerated = true;
                    forwardAdaptersSeen = type
                        .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Count(m => m.ReturnType == typeof(Observation));
                }

                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    bool takesObservation = method.GetParameters()
                        .Any(p => p.ParameterType == typeof(Observation)
                               || p.ParameterType == typeof(Observation[])
                               || p.ParameterType == typeof(IReadOnlyList<Observation>)
                               || p.ParameterType == typeof(IEnumerable<Observation>));
                    if (!takesObservation) continue;

                    scanned++;
                    if (Array.Exists(resultModels, m => m.IsAssignableFrom(method.ReturnType)))
                        offenders.Add($"{type.FullName}.{method.Name} -> {method.ReturnType.Name}");
                }
            }
        }

        // NON-VACUITY. Assert.Empty over a scan that matched nothing is indistinguishable from a
        // scan that found nothing wrong, and it fails in the flattering direction. Prove the scan
        // actually reached methods taking an Observation before believing the empty list.
        //
        // ⚠ AND `scanned > 0` ALONE IS NOT THAT PROOF — it was satisfied by RepCollapse and
        // PairedEvalComparer, which live in the meta assembly and are not adapters, so it stayed
        // green while the adapter assembly went unread. A denominator that can be filled by
        // something other than the artifact under test is a diluted denominator. Assert on the
        // ADAPTER type specifically, and on the assembly set being genuinely two.
        Assert.True(scanned > 0, "the reflection scan matched no Observation-consuming method at all");
        Assert.True(
            assemblies.Count == 2,
            $"the one-way scan must cover BOTH the meta assembly and the adapter assembly; it "
            + $"resolved to {assemblies.Count} distinct assembly/assemblies: "
            + string.Join(", ", assemblies.Select(a => a.GetName().Name)));
        Assert.True(
            adapterTypeEnumerated,
            "the scan never enumerated ObservationAdapters — the type Slice 2.2 is ABOUT. An empty "
            + "offender list means nothing until the assembly holding the adapters is actually read.");
        Assert.True(
            forwardAdaptersSeen == 3,
            $"Slice 2.2 ships exactly three forward projections (EvalResult, MetricResult, "
            + $"M.E.AI EvaluationResult); the scan found {forwardAdaptersSeen}. A count that is not "
            + "three means the scan is reading a different type than the one under test.");
        Assert.Empty(offenders);
    }

    // ── EvalResult → Observation ──────────────────────────────────────────────────────────────

    [Fact]
    public void EvalResult_Measured_CarriesItsValue()
    {
        var observation = ResultWith(new EvalScore(0.8, null, "pass", true, 0.5, "none", null))
            .ToObservation("c1", "live");

        Assert.Equal("c1", observation.CaseId);
        Assert.Equal("live", observation.ArmId);
        Assert.Equal(0.8, observation.Value, 12);
        Assert.Equal(MeasurementState.Measured, observation.State);
    }

    [Fact]
    public void EvalResult_Skipped_IsNotMeasured_NotAZero()
    {
        // The label carries the fact today (Measurement stays Measured until Slice 1.4's schema
        // widening lands), and the adapter reads BOTH halves through CensusBucket so it does not
        // have to be re-taught when the schema catches up.
        var skipped = ResultWith(new EvalScore(0.0, null, "skipped", false, null, "none", null))
            .ToObservation("c1", "live");
        var errored = ResultWith(new EvalScore(0.0, null, "error", false, null, "none", null))
            .ToObservation("c1", "live");

        Assert.Equal(MeasurementState.NotMeasured, skipped.State);
        Assert.Equal(MeasurementState.NotMeasured, errored.State);
        Assert.False(skipped.CountsTowardAggregate);
    }

    [Fact]
    public void EvalResult_Inapplicable_IsNotApplicable()
    {
        var observation = EvalScore.NotApplicable() is var score
            ? ResultWith(score).ToObservation("c1", "live")
            : default;

        Assert.Equal(MeasurementState.NotApplicable, observation.State);
        Assert.False(observation.CountsTowardAggregate);
    }

    // ── MetricResult → Observation ────────────────────────────────────────────────────────────

    [Fact]
    public void MetricResult_IsNormalisedFrom0To100()
    {
        // ⚠ MetricResult.Score is documented 0..100 and EvalScore.Value is 0..1. The meta lane pairs
        // them side by side; without this division a 0..100 arm beats a 0..1 arm on every case and
        // the wins table looks entirely plausible.
        var observation = MetricResult.Pass("m", 80.0).ToObservation("c1", "metric-arm");

        Assert.Equal(0.8, observation.Value, 12);
        Assert.Equal(MeasurementState.Measured, observation.State);

        Assert.Equal(0.0, MetricResult.Fail("m", "nope").ToObservation("c1", "metric-arm").Value, 12);
    }

    [Fact]
    public void MetricResult_WithANonFiniteScore_IsNotMeasured()
    {
        var broken = new MetricResult { MetricName = "m", Score = double.NaN };

        Assert.Equal(MeasurementState.NotMeasured, broken.ToObservation("c1", "metric-arm").State);
    }

    // ── M.E.AI EvaluationResult → Observation ─────────────────────────────────────────────────

    [Fact]
    public void MeaiNumericMetric_IsNormalisedFromTheOneToFiveScale()
    {
        var result = new MeaiEvaluationResult(new NumericMetric("quality", 5.0));
        Assert.Equal(1.0, result.ToObservation("c1", "judge").Value, 12);

        Assert.Equal(0.0, new MeaiEvaluationResult(new NumericMetric("quality", 1.0))
            .ToObservation("c1", "judge").Value, 12);
        Assert.Equal(0.5, new MeaiEvaluationResult(new NumericMetric("quality", 3.0))
            .ToObservation("c1", "judge").Value, 12);
    }

    [Fact]
    public void MeaiMetricWithNoValue_IsNotMeasured_NotAZero()
    {
        // M.E.AI's first-class "the judge could not speak": an unparseable response, a content
        // filter, an evaluator error. Scoring it 0.0 is reporting an instrument that did not run as
        // an arm that failed — the defect this whole lane exists to prevent.
        var noValue = new MeaiEvaluationResult(new NumericMetric("quality"));

        var observation = noValue.ToObservation("c1", "judge");
        Assert.Equal(MeasurementState.NotMeasured, observation.State);
        Assert.False(observation.CountsTowardAggregate);

        var noBoolean = new MeaiEvaluationResult(new BooleanMetric("grounded"));
        Assert.Equal(MeasurementState.NotMeasured, noBoolean.ToObservation("c1", "judge").State);
    }

    [Fact]
    public void MeaiBooleanMetric_IsZeroOrOne()
    {
        Assert.Equal(1.0, new MeaiEvaluationResult(new BooleanMetric("grounded", true))
            .ToObservation("c1", "judge").Value);
        Assert.Equal(0.0, new MeaiEvaluationResult(new BooleanMetric("grounded", false))
            .ToObservation("c1", "judge").Value);
    }

    [Fact]
    public void MeaiNamedMetric_IsSelectable_AndAMissingNameIsNotMeasured()
    {
        var result = new MeaiEvaluationResult(
            new NumericMetric("quality", 5.0), new NumericMetric("safety", 1.0));

        Assert.Equal(0.0, result.ToObservation("c1", "judge", "safety").Value, 12);
        Assert.Equal(MeasurementState.NotMeasured, result.ToObservation("c1", "judge", "absent").State);
    }
}
