// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
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
