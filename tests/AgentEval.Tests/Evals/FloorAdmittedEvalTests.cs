// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// AE-04, second half: the door only opens with a chance floor, and the floor reaches the result.
/// </summary>
/// <remarks>
/// Every refusal is ablated in BOTH directions — the refusing case AND the admitting case — because a
/// check never observed failing is not evidence, and a check never observed passing is a wall.
/// </remarks>
public class FloorAdmittedEvalTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class StubEval : IEval
    {
        private readonly Func<EvalInput, EvalResult> _produce;

        public StubEval(string key = "stub_eval", Func<EvalInput, EvalResult>? produce = null)
        {
            Key = key;
            _produce = produce ?? (_ => PlainResult(key));
        }

        public string Key { get; }
        public string Name => "Stub Eval";
        public string Category => "test";
        public string Version => "1.0.0";
        public int Invocations { get; private set; }

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            Invocations++;
            return Task.FromResult(_produce(input));
        }
    }

    private static EvalResult PlainResult(string key = "stub_eval", double value = 0.9) => new(
        Metric: new(key, "Stub Eval", "test", "1.0.0"),
        Score: new(value, null, "pass", true, null, "none", null),
        Details: new(null, null, null, null, null),
        Provenance: new("atomic-code", null, null, null, null, 0.0, false),
        EvaluatedAt: DateTimeOffset.Parse("2026-09-07T00:00:00Z"));

    private static readonly ChanceFloor Derived = ChanceFloor.UniformChoice(4);
    private static readonly EvalInput Input = new("q");

    // ══════════════════════════════════════════════════════════════════════════
    // ABLATION 1 — an eval with NO floor is refused, and the message names the eval
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnEvalOfferedWithNoFloor_IsRefused_AndTheMessageNamesIt()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => FloorAdmittedEval.Admit(new StubEval("task_completion"), null!));

        Assert.Contains("task_completion", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NO chance floor", ex.Message, StringComparison.Ordinal);
        Assert.Equal("floor", ex.ParamName);
    }

    [Fact]
    public void TheOtherDirection_AnEvalWithAFloorIsAdmitted()
    {
        var admitted = FloorAdmittedEval.Admit(new StubEval("task_completion"), Derived);

        Assert.Equal("task_completion", admitted.Key);
        Assert.Same(Derived, admitted.Floor);
    }

    [Fact]
    public void ThereIsNoFloorlessOverload_TheDoorHasExactlyOneShape()
    {
        // The prohibited state is unreachable rather than waived: an eval cannot be admitted without
        // presenting a floor, so no admission path exists that "forgets" one.
        var doors = typeof(FloorAdmittedEval)
            .GetMethods()
            .Where(m => m.Name == nameof(FloorAdmittedEval.Admit))
            .ToList();

        Assert.Single(doors);
        Assert.Equal(
            new[] { typeof(IEval), typeof(ChanceFloor) },
            doors[0].GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void ANullEvalIsRefusedBeforeTheFloorIsEvenRead()
    {
        Assert.Throws<ArgumentNullException>(() => FloorAdmittedEval.Admit(null!, Derived));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ABLATION 2 — a NotDerivable floor with an EMPTY derivation is refused
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ANotDerivableFloorWithNoStatedReason_IsRefused(string blank)
    {
        // ChanceFloor.NotDerivable(reason) refuses a blank reason itself, but the primary record
        // constructor and a `with` copy both reach this state, so the door must check it too.
        var floorless = new ChanceFloor(
            ChanceFloor.KindNotDerivable, FloorState.NotDerivable, double.NaN, null, 0, 0, blank);

        var ex = Assert.Throws<ArgumentException>(
            () => FloorAdmittedEval.Admit(new StubEval("safety_gate"), floorless));

        Assert.Contains("safety_gate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NO derivation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWithCopyThatBlanksTheDerivation_IsAlsoRefused()
    {
        var blanked = ChanceFloor.NotDerivable("no pool exists") with { Derivation = "  " };

        Assert.Throws<ArgumentException>(() => FloorAdmittedEval.Admit(new StubEval(), blanked));
    }

    [Fact]
    public void ADerivedFloorWithNoDerivation_IsAlsoRefused()
    {
        // Stronger than the stated rule, and the library already agrees: ComparabilityOf refuses to
        // promote a bar that arrives with no chance-floor evidence beside it, recording NotDerivable
        // instead. Admitting one here would produce a result that downgrades itself at persistence.
        var barWithoutDerivation = new ChanceFloor(
            ChanceFloor.KindUniformChoice, FloorState.Derived, 0.25, null, 1, 4, "");

        var ex = Assert.Throws<ArgumentException>(
            () => FloorAdmittedEval.Admit(new StubEval(), barWithoutDerivation));

        Assert.Contains("NO derivation", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void ADerivedFloorWhoseBarIsNotAProbability_IsRefused(double bar)
    {
        var bogus = new ChanceFloor(
            ChanceFloor.KindUniformChoice, FloorState.Derived, bar, null, 1, 4, "a stated derivation");

        var ex = Assert.Throws<ArgumentException>(() => FloorAdmittedEval.Admit(new StubEval("odd"), bogus));

        Assert.Contains("odd", ex.Message, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ABLATION 3 — a NotDerivable floor WITH a reason is admitted, and the reason
    //              is on the result
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ANotDerivableFloorWithAStatedReason_IsAdmitted_AndTheReasonReachesTheResult()
    {
        const string Reason = "the response is free text over an unbounded space; there is no pool to draw from";
        var admitted = FloorAdmittedEval.Admit(new StubEval(), ChanceFloor.NotDerivable(Reason));

        var result = await admitted.EvaluateAsync(Input);

        var evidence = Assert.Single(
            result.Details.Evidence!,
            e => e.Source == ComparabilityFacts.ChanceFloorEvidenceSource);
        Assert.Equal(ChanceFloor.KindNotDerivable, evidence.Reference);
        Assert.Equal(Reason, evidence.Message);
    }

    [Fact]
    public async Task ANotDerivableFloor_WritesNoNumber_BecauseAnAbsentFloorIsNotAZeroFloor()
    {
        var admitted = FloorAdmittedEval.Admit(new StubEval(), ChanceFloor.NotDerivable("no pool exists"));

        var result = await admitted.EvaluateAsync(Input);

        Assert.True(
            result.Details.Dimensions is null
            || !result.Details.Dimensions.ContainsKey(ComparabilityFacts.ChanceFloorDimension),
            "A not-derivable floor must write no chance_floor dimension. A zero there condemns a metric at p = 0.70.");
    }

    [Fact]
    public async Task ADerivedFloor_WritesBothHalvesOfTheConvention()
    {
        var admitted = FloorAdmittedEval.Admit(new StubEval(), ChanceFloor.UniformChoice(4));

        var result = await admitted.EvaluateAsync(Input);

        Assert.Equal(0.25, result.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension], 10);
        var evidence = Assert.Single(
            result.Details.Evidence!,
            e => e.Source == ComparabilityFacts.ChanceFloorEvidenceSource);
        Assert.Equal(ChanceFloor.KindUniformChoice, evidence.Reference);
        Assert.Contains("4 alternatives", evidence.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEstimatedFloor_RecordsTheIntervalBound_NotThePointEstimate()
    {
        // Comparing an observed rate to a point estimate computed from the same corpus is the
        // co-moving-operands failure; ComparisonBar is the interval's upper bound when estimated.
        var estimated = ChanceFloor.Empirical(successes: 3, trials: 10, policiesConsidered: 1);
        var admitted = FloorAdmittedEval.Admit(new StubEval(), estimated);

        var result = await admitted.EvaluateAsync(Input);

        Assert.True(estimated.WasEstimated);
        Assert.Equal(estimated.ComparisonBar, result.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension], 10);
        Assert.NotEqual(estimated.Value, result.Details.Dimensions[ComparabilityFacts.ChanceFloorDimension], 10);
    }

    [Fact]
    public async Task AnnotationPreservesTheWrappedEvalsOwnVerdictAndItsOtherEvidence()
    {
        var withOwnData = new StubEval(produce: _ => PlainResult() with
        {
            Details = new(
                Dimensions: new Dictionary<string, double> { ["clarity"] = 0.8 },
                Evidence: new[] { new EvalEvidence("response", "r", "m") },
                Recommendations: null, SubResults: null, AggregationStrategy: null),
        });
        var admitted = FloorAdmittedEval.Admit(withOwnData, Derived);

        var result = await admitted.EvaluateAsync(Input);

        Assert.Equal(0.9, result.Score.Value, 10);
        Assert.Equal(0.8, result.Details.Dimensions!["clarity"], 10);
        Assert.Equal(0.25, result.Details.Dimensions[ComparabilityFacts.ChanceFloorDimension], 10);
        Assert.Equal(2, result.Details.Evidence!.Count);
    }

    [Fact]
    public void TheWrappedEvalsIdentityIsPassedThroughUnchanged()
    {
        var inner = new StubEval("task_completion");
        var admitted = FloorAdmittedEval.Admit(inner, Derived);

        Assert.Equal(inner.Key, admitted.Key);
        Assert.Equal(inner.Name, admitted.Name);
        Assert.Equal(inner.Category, admitted.Category);
        Assert.Equal(inner.Version, admitted.Version);
        Assert.Same(inner, admitted.Inner);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ABLATION 5 — SELF-EXAMINATION: the floor may not come from the eval's output
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TheFloorDoesNotMoveWhenTheEvalsOutputMoves()
    {
        // The floor is supplied at admission and never read back off a result, so an eval cannot
        // influence the bar it is judged against by changing what it returns.
        int call = 0;
        var swinging = new StubEval(produce: _ => PlainResult(value: call++ == 0 ? 0.05 : 0.99));
        var admitted = FloorAdmittedEval.Admit(swinging, Derived);

        var low = await admitted.EvaluateAsync(Input);
        var high = await admitted.EvaluateAsync(Input);

        Assert.NotEqual(low.Score.Value, high.Score.Value);
        Assert.Equal(
            low.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension],
            high.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension],
            10);
    }

    [Fact]
    public async Task AnEvalThatReportsItsOwnFloorDimension_IsRefusedAtEvaluationTime()
    {
        var selfScoring = new StubEval("self_scoring", _ => PlainResult() with
        {
            Details = new(
                Dimensions: new Dictionary<string, double> { [ComparabilityFacts.ChanceFloorDimension] = 0.01 },
                Evidence: null, Recommendations: null, SubResults: null, AggregationStrategy: null),
        });
        var admitted = FloorAdmittedEval.Admit(selfScoring, Derived);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => admitted.EvaluateAsync(Input));

        Assert.Contains("self_scoring", ex.Message, StringComparison.Ordinal);
        Assert.Contains("may not be supplied by the eval", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEvalThatReportsItsOwnFloorEvidence_IsAlsoRefused()
    {
        var selfScoring = new StubEval("self_evidence", _ => PlainResult() with
        {
            Details = new(
                Dimensions: null,
                Evidence: new[] { new EvalEvidence(ComparabilityFacts.ChanceFloorEvidenceSource, "k", "mine") },
                Recommendations: null, SubResults: null, AggregationStrategy: null),
        });
        var admitted = FloorAdmittedEval.Admit(selfScoring, Derived);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => admitted.EvaluateAsync(Input));

        Assert.Contains("self_evidence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOtherDirection_AnEvalThatReportsOtherDimensionsIsFine()
    {
        var wellBehaved = new StubEval(produce: _ => PlainResult() with
        {
            Details = new(
                Dimensions: new Dictionary<string, double> { ["clarity"] = 0.8 },
                Evidence: new[] { new EvalEvidence("response", "r", "m") },
                Recommendations: null, SubResults: null, AggregationStrategy: null),
        });

        var result = await FloorAdmittedEval.Admit(wellBehaved, Derived).EvaluateAsync(Input);

        Assert.Equal(0.25, result.Details.Dimensions![ComparabilityFacts.ChanceFloorDimension], 10);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The meta-lane rule is not broken by this type
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AcrossEveryAgentEvalAssembly_TheOnlyIEvalCarryingAChanceFloorIsTheDoor()
    {
        // The prose version of this claim shipped with three WRONG numbers (74 IEval files, 4
        // ChanceFloor files, "intersection zero") because a count in a doc comment goes stale with
        // nobody watching. This is the same claim as an assertion, so it cannot.
        var assemblies = LoadedAgentEvalAssemblies();

        var evals = assemblies
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IEval).IsAssignableFrom(t))
            .ToList();

        var carryAFloor = evals
            .Where(t => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Any(pr => pr.PropertyType == typeof(ChanceFloor))
                     || t.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Any(f => f.FieldType == typeof(ChanceFloor)))
            .ToList();

        // ⚠ Positive control FIRST. "Exactly one carries a floor" is also true of a scan that found
        // one type, or none — the shape that has shipped six times in this repository.
        Assert.True(evals.Count > 50, $"the scan found only {evals.Count} IEval implementations, so it measured nothing");

        Assert.Equal(new[] { typeof(FloorAdmittedEval) }, carryAFloor);
    }

    private static List<System.Reflection.Assembly> LoadedAgentEvalAssemblies()
    {
        var seen = new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
        var queue = new Queue<System.Reflection.Assembly>();
        queue.Enqueue(typeof(FloorAdmittedEvalTests).Assembly);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var reference in current.GetReferencedAssemblies())
            {
                if (reference.Name is null
                    || !reference.Name.StartsWith("AgentEval", StringComparison.Ordinal)
                    || reference.Name.EndsWith(".Tests", StringComparison.Ordinal)
                    || seen.ContainsKey(reference.Name))
                {
                    continue;
                }

                var loaded = System.Reflection.Assembly.Load(reference);
                seen[reference.Name] = loaded;
                queue.Enqueue(loaded);
            }
        }

        return seen.Values.ToList();
    }

    private static IEnumerable<Type> SafeTypes(System.Reflection.Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    [Fact]
    public void ThisTypeDoesNotLiveInTheMetaNamespace()
    {
        // ADR-030 §3.2: meta-evaluation never implements IEval, enforced by namespace in
        // MetaLaneArchitectureTests. This wrapper returns the WRAPPED eval's verdict, not a floor's.
        Assert.False(
            typeof(FloorAdmittedEval).Namespace!.StartsWith("AgentEval.Evals.Meta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWrappedEvalIsCalledExactlyOnce_TheDoorIsNotAJudge()
    {
        var inner = new StubEval();

        await FloorAdmittedEval.Admit(inner, Derived).EvaluateAsync(Input);

        Assert.Equal(1, inner.Invocations);
    }

    [Fact]
    public async Task CancellationIsPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelling = new StubEval(produce: _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FloorAdmittedEval.Admit(cancelling, Derived).EvaluateAsync(Input, cts.Token));
    }
}
