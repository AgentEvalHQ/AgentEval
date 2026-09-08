// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.MAF.Evaluators;
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Unit coverage for <see cref="AgentEvalCompositeEvaluator"/>: it captures the rich
/// <see cref="EvalResult"/> tree, flattens it into MEAI metrics (root <c>(overall)</c> + each atomic
/// leaf), and disambiguates duplicate leaf names.
/// </summary>
public class AgentEvalCompositeEvaluatorTests
{
    /// <summary>An <see cref="IEval"/> that returns a fixed tree (no LLM).</summary>
    private sealed class StubComposite : IEval
    {
        private readonly EvalResult _tree;
        public StubComposite(EvalResult tree) => _tree = tree;
        public string Key => "stub";
        public string Name => "StubComposite";
        public string Category => "agentic";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(_tree);
    }

    private static EvalResult Leaf(string name, double value) => new(
        new EvalMetadata(name, name, "quality", "1.0.0"),
        new EvalScore(value, null, value >= 0.7 ? "pass" : "fail", value >= 0.7, 0.7, value >= 0.7 ? "none" : "high", null),
        new EvalDetails(null, null, null, null, null),
        new EvalProvenance("atomic-llm", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);

    private static EvalResult Tree(params EvalResult[] subs) => new(
        new EvalMetadata("root", "StubComposite", "agentic", "1.0.0"),
        new EvalScore(subs.Average(s => s.Score.Value), null, "pass", true, 0.7, "none", null),
        new EvalDetails(null, null, null, subs, "mean"),
        new EvalProvenance("composite", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);

    private static ValueTask<EvaluationResult> Run(EvalResult tree, AgentEvalCompositeEvaluator evaluator) =>
        evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "r")));

    [Fact]
    public async Task EvaluateAsync_CapturesTree_AndFlattensRootPlusLeaves()
    {
        var tree = Tree(Leaf("relevance", 0.9), Leaf("coherence", 0.8));
        var evaluator = new AgentEvalCompositeEvaluator(new StubComposite(tree));

        var result = await Run(tree, evaluator);

        // The rich tree is captured for rendering...
        Assert.Single(evaluator.CapturedResults);
        Assert.Same(tree, evaluator.CapturedResults[0]);
        // ...and flattened to MEAI: the root "(overall)" metric plus each leaf.
        Assert.Contains("StubComposite (overall)", result.Metrics.Keys);
        Assert.Contains("relevance", result.Metrics.Keys);
        Assert.Contains("coherence", result.Metrics.Keys);
    }

    [Fact]
    public async Task EvaluateAsync_DisambiguatesDuplicateLeafNames()
    {
        var tree = Tree(Leaf("relevance", 0.9), Leaf("relevance", 0.5));   // same name twice
        var evaluator = new AgentEvalCompositeEvaluator(new StubComposite(tree));

        var result = await Run(tree, evaluator);

        Assert.Contains("relevance", result.Metrics.Keys);
        Assert.Contains(result.Metrics.Keys, k => k.StartsWith("relevance #", StringComparison.Ordinal));
    }
}

/// <summary>
/// 7.2 — the MAF door DECLARES a floor and counts the floored leaves; it applies neither.
/// </summary>
public class AgentEvalCompositeEvaluatorFloorTests
{
    private sealed class Leaf(string key, bool floored) : AtomicCodeEval(key, key, "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input) => Build(1.0, true, "none");

        public IEval AsAdmitted() => floored
            ? FloorAdmittedEval.Admit(this, ChanceFloor.UniformChoice(4))
            : this;
    }

    private static CompositeEval Composite(params bool[] flooredPerLeaf) =>
        new("root", "Root", "test", "1.0.0",
            [.. flooredPerLeaf.Select((f, i) => new EvalComponent(new Leaf($"k{i}", f).AsAdmitted()))],
            WeightedSumAggregation.Instance);

    private static async Task<AgentEvalCompositeEvaluator> RunAsync(
        CompositeEval composite, ChanceFloor? rootFloor = null)
    {
        var evaluator = new AgentEvalCompositeEvaluator(composite, rootFloor);
        await evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")));
        return evaluator;
    }

    [Fact]
    public async Task AFloorlessCompositeReportsZeroFlooredLeaves()
    {
        // Before 7.2 a floorless MAF composite and a fully floored one rendered identically.
        var evaluator = await RunAsync(Composite(false, false, false));

        Assert.Equal(3, evaluator.LeafCount);
        Assert.Equal(0, evaluator.FlooredLeafCount);
    }

    [Fact]
    public async Task AFullyFlooredCompositeReportsAllOfThem()
    {
        var evaluator = await RunAsync(Composite(true, true));

        Assert.Equal(2, evaluator.LeafCount);
        Assert.Equal(2, evaluator.FlooredLeafCount);
    }

    [Fact]
    public async Task APartlyFlooredCompositeIsNotRoundedEitherWay()
    {
        var evaluator = await RunAsync(Composite(true, false, true, false));

        Assert.Equal(4, evaluator.LeafCount);
        Assert.Equal(2, evaluator.FlooredLeafCount);
    }

    [Fact]
    public async Task TheCountIsReadOffTheTree_NotOffTheDeclaration()
    {
        // A confident root declaration over leaves nobody admitted must still report 0.
        var evaluator = await RunAsync(
            Composite(false, false),
            ChanceFloor.UniformChoice(2));

        Assert.Equal(0, evaluator.FlooredLeafCount);
        Assert.NotNull(evaluator.DeclaredRootFloor);
    }

    [Fact]
    public async Task TheDeclarationIsRecorded_AndSaysItGatesNothing()
    {
        var evaluator = await RunAsync(Composite(true), ChanceFloor.UniformChoice(4));
        var result = await evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")));

        var metric = result.Metrics[AgentEvalCompositeEvaluator.FloorDeclarationMetricName];

        Assert.Contains("RECORDED and NOT APPLIED", metric.Reason, StringComparison.Ordinal);
        Assert.Contains("0.2500", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDeclaredFloor_IsAThirdState_NotNotDerivable()
    {
        // "nobody asked" and "asked and could not answer" are different facts.
        var evaluator = await RunAsync(Composite(true));
        var result = await evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")));

        var metric = result.Metrics[AgentEvalCompositeEvaluator.FloorDeclarationMetricName];

        Assert.Null(evaluator.DeclaredRootFloor);
        Assert.Contains("nobody asked", metric.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT DERIVABLE", metric.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFloorWithNoDerivation_IsRefusedAtConstruction()
    {
        var blank = ChanceFloor.UniformChoice(4) with { Derivation = "   " };

        var ex = Assert.Throws<ArgumentException>(
            () => new AgentEvalCompositeEvaluator(Composite(true), blank));

        Assert.Contains("without its reason", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScoreIsUNCHANGED_ByTheDeclaration()
    {
        // The load-bearing half of "recorded, not applied": the same composite must produce the same
        // root score with and without a floor. A floor that moved a number would be Q6 answered by
        // accident.
        var withoutFloor = await RunAsync(Composite(true, false));
        var withFloor = await RunAsync(Composite(true, false), ChanceFloor.UniformChoice(2));

        Assert.Equal(
            withoutFloor.CapturedResults[0].Score.Value,
            withFloor.CapturedResults[0].Score.Value,
            12);
        Assert.Equal(
            withoutFloor.CapturedResults[0].Score.Passed,
            withFloor.CapturedResults[0].Score.Passed);
    }
}
