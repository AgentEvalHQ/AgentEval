// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Text.Json;
using AgentEval.Evals.Meta;
using AgentEval.Output;

/// <summary>
/// Maps recursive EvalResult trees into the flat ScenarioResult shape that
/// IOutputStore.WriteScenarioResultAsync persists. Score, pass-state, dimensions,
/// and cost are lifted to top-level ScenarioResult fields for queryability;
/// the full recursive tree is preserved as JSON inside ScenarioResult.Output.
/// </summary>
public static class EvalResultPersistence
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Builds a ScenarioResult that stores <paramref name="result"/> as JSON in Output.</summary>
    /// <param name="result">The eval-result tree to persist.</param>
    /// <param name="scenarioId">Scenario identifier.</param>
    /// <param name="scenarioName">Human-readable scenario name.</param>
    /// <param name="assertions">
    /// Assertion outcomes to persist alongside the scenario. When <see langword="null"/> the
    /// outcomes collected by the ambient <see cref="AgentEvalScope"/> are used, so an eval that
    /// runs its assertions inside <c>AgentEvalScope.Collecting()</c> gets them into the artifact
    /// with no extra wiring. Pass an empty list to persist none. (AE-01: before this, the field was
    /// hard-coded empty and assertion outcomes reached no artifact AgentEval writes.)
    /// </param>
    /// <param name="input">
    /// The stimulus the agent was given, when the caller knows it. ADR-031 S2: an <c>EvalResult</c>
    /// does not carry its own input, so this cannot be derived here and the caller must pass it.
    /// <para>
    /// ⚠ <b>It defaults to the empty string, which is what this method hard-coded before.</b> A
    /// caller that does not pass one produces a <b>byte-identical</b> <c>ScenarioResult</c>:
    /// <c>Input</c> is unchanged and <c>StimulusHash</c> stays null, which the store omits. No
    /// stored content hash moves because of this parameter existing.
    /// </para>
    /// </param>
    /// <param name="subjectModel">
    /// The model the SUBJECT ran on, when the caller knows it. Used for one thing only: deciding
    /// whether the judge that graded this result is the subject's own model
    /// (<see cref="JudgeSubjectRelation"/>). ADR-031 §0.1's <c>judgeIsSubjectModel</c> follow-on.
    /// <para>
    /// ⚠ <b>Not supplying it yields <see cref="JudgeSubjectRelation.Unknown"/>, never
    /// <see cref="JudgeSubjectRelation.DifferentModel"/>.</b> A bool here would answer "nobody told
    /// us" with "the judge is a different model", which is the flattering direction.
    /// </para>
    /// </param>
    public static ScenarioResult ToScenarioResult(
        EvalResult result,
        string scenarioId,
        string scenarioName,
        IReadOnlyList<AssertionResult>? assertions = null,
        string? input = null,
        string? subjectModel = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);

        var json = JsonSerializer.Serialize(result, s_jsonOpts);

        // Build a Metrics dictionary that lifts queryable-from-the-flat-shape
        // fields out of the recursive tree: dimensions (already there);
        // confidence + severity-as-ordinal so callers querying the store
        // by `ScenarioResult.Metrics` don't have to deserialise the JSON
        // tree. Severity is encoded as a small ordinal {none=0, low=1,
        // medium=2, high=3, critical=4} for numeric comparison.
        //
        // Lifted keys are prefixed with `_lifted.` to avoid silently
        // overwriting consumer-supplied Dimensions that legitimately use
        // domain words like "confidence" or "severity_ordinal" as criterion
        // names. Reads must use the `_lifted.*` form.
        const string ConfidenceKey      = "_lifted.confidence";
        const string SeverityOrdinalKey = "_lifted.severity_ordinal";

        var metrics = result.Details.Dimensions is { } dims
            ? new Dictionary<string, double>(dims)
            : new Dictionary<string, double>();
        if (result.Score.Confidence is { } conf)
            metrics[ConfidenceKey] = conf;
        metrics[SeverityOrdinalKey] = result.Score.Severity switch
        {
            "critical" => 4,
            "high"     => 3,
            "medium"   => 2,
            "low"      => 1,
            _          => 0,
        };

        return new ScenarioResult(
            Id: scenarioId,
            Name: scenarioName,
            Input: input ?? "",
            Output: json,
            Passed: result.Score.Passed,
            Score: result.Score.Value,
            Metrics: metrics,
            Assertions: assertions
                ?? AgentEval.Assertions.AgentEvalScope.Current?.Results
                ?? Array.Empty<AssertionResult>(),
            Duration: TimeSpan.Zero,
            EstimatedCost: result.Provenance.EstimatedCost)
        {
            // Null when no input was supplied — and null must be read as "nobody computed one",
            // never as "the inputs match". StimulusHash.SameStimulus refuses a null on either side
            // for exactly that reason.
            StimulusHash = StimulusHash.Of(input),

            // ADR-031 V1's other five facts. Every one is read off `result`, which the runner
            // already holds — nothing here asks a caller for anything it does not have, which is
            // V1's whole claim: comparability data belongs on the RUN, not in a manifest.
            Comparability = ComparabilityOf(result, subjectModel),
        };
    }

    /// <summary>
    /// Reads ADR-031 V1's five non-stimulus comparability facts off an eval result.
    /// </summary>
    /// <param name="result">The result being persisted.</param>
    /// <param name="subjectModel">The subject's model, when the caller knows it.</param>
    /// <returns>The facts.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Every absence here is recorded as an absence.</b> An eval with no threshold gets a null
    /// <c>EffectiveBar</c>, not 0.0; an eval with no declared floor gets a null
    /// <c>ChanceFloor</c>, not a zero one; an eval with no judge gets a null <c>Judge</c>, which is
    /// "deterministic", not "unknown judge". Each of those three collapses is a way of making two
    /// runs look comparable when nobody checked.
    /// </para>
    /// <para>
    /// ⚠ <b>The floor is read from the convention ADR-030 §3.2 rules, not from a field on the
    /// score.</b> <c>EvalScore.ChanceFloor</c> was CUT: floors live in
    /// <c>Details.Dimensions["chance_floor"]</c> plus one
    /// <c>EvalEvidence("chance-floor", kind, derivation)</c>. A dimension with no evidence beside it
    /// is a number with no derivation, and per ADR-030 that number is unusable — so it is recorded
    /// as <see cref="FloorState.NotDerivable"/> with the reason, never as a bar.
    /// </para>
    /// </remarks>
    internal static ComparabilityFacts ComparabilityOf(EvalResult result, string? subjectModel)
    {
        var evidence = result.Details.Evidence?
            .FirstOrDefault(e => string.Equals(
                e.Source, ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal));

        bool hasBar = result.Details.Dimensions is { } dims
            && dims.TryGetValue(ComparabilityFacts.ChanceFloorDimension, out double bar)
            && double.IsFinite(bar);
        double barValue = hasBar
            ? result.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension]
            : double.NaN;

        RecordedChanceFloor? floor = (hasBar, evidence) switch
        {
            // Both halves of the convention present: a bar with its derivation behind it.
            (true, { } ev) => new RecordedChanceFloor(ev.Reference, FloorState.Derived, barValue, ev.Message),

            // A bar with no derivation. ADR-030 §3.2: "the number without its derivation is
            // unusable" — so it is NOT promoted to a bar, and the absence is what gets recorded.
            (true, null) => new RecordedChanceFloor(
                AgentEval.Evals.Meta.ChanceFloor.KindNotDerivable,
                FloorState.NotDerivable,
                null,
                $"a '{ComparabilityFacts.ChanceFloorDimension}' dimension was recorded with no "
                + $"'{ComparabilityFacts.ChanceFloorEvidenceSource}' evidence beside it, so the number has no "
                + "derivation and cannot be used as a bar"),

            // A derivation saying why no floor exists. This is the state worth carrying: somebody
            // asked and could not answer, which is not the same as nobody asking.
            (false, { } ev) => new RecordedChanceFloor(ev.Reference, FloorState.NotDerivable, null, ev.Message),

            // Nobody derived one at all. Null, never a zero floor.
            (false, null) => null,
        };

        JudgeFingerprint? judge = string.IsNullOrWhiteSpace(result.Provenance.JudgeModel)
            ? null
            : JudgeFingerprint.For(
                result.Provenance.JudgeModel!,
                rubricDigest: result.Provenance.PromptHash,
                subjectModel: subjectModel);

        return new ComparabilityFacts(result.Metric.Key, result.Metric.Version)
        {
            EffectiveBar = result.Score.Threshold,
            ChanceFloor = floor,
            Judge = judge,
        };
    }

    /// <summary>Restores an EvalResult from a ScenarioResult previously produced by <see cref="ToScenarioResult"/>.</summary>
    public static EvalResult? FromScenarioResult(ScenarioResult sr)
    {
        ArgumentNullException.ThrowIfNull(sr);
        if (string.IsNullOrEmpty(sr.Output)) return null;
        try
        {
            return JsonSerializer.Deserialize<EvalResult>(sr.Output, s_jsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
