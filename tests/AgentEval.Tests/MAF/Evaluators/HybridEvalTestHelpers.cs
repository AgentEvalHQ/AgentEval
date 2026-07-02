// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Shared in-memory fakes for the hybrid-eval tests (<c>CompositeAgentEvaluator</c> /
/// <c>UnifiedEvalReport</c>) — no LLM, no network, so the whole suite runs in milliseconds.
/// </summary>
internal static class HybridEvalTestHelpers
{
    /// <summary>Builds <paramref name="n"/> minimal <see cref="EvalItem"/>s (query/response only).</summary>
    public static IReadOnlyList<EvalItem> Items(int n) =>
        Enumerable.Range(0, n).Select(i => new EvalItem($"q{i}", $"r{i}")).ToList();

    /// <summary>A passing <see cref="AgentEvaluationResults"/> with one numeric metric per item.</summary>
    public static AgentEvaluationResults Pass(IReadOnlyList<EvalItem> items, string source, string metric = "relevance")
    {
        var results = items.Select(_ =>
        {
            var r = new EvaluationResult();
            r.Metrics[metric] = new NumericMetric(metric, 5.0, "ok")
            { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Good, failed: false, reason: "ok") };
            return r;
        }).ToList();
        return new AgentEvaluationResults(source, results, inputItems: items);
    }

    /// <summary>Fake <see cref="IAgentEvaluator"/> whose behaviour is a lambda; counts invocations.</summary>
    public sealed class FakeEvaluator(
        string name,
        Func<IReadOnlyList<EvalItem>, CancellationToken, Task<AgentEvaluationResults>> body) : IAgentEvaluator
    {
        private int _calls;
        public string Name { get; } = name;
        public int Calls => Volatile.Read(ref _calls);
        public Task<AgentEvaluationResults> EvaluateAsync(IReadOnlyList<EvalItem> items, string evalName, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return body(items, ct);
        }
    }

    public static FakeEvaluator Fast(string s) => new(s, (items, _) => Task.FromResult(Pass(items, s)));
    public static FakeEvaluator Slow(string s, int ms) => new(s, async (items, ct) => { await Task.Delay(ms, ct); return Pass(items, s); });
    public static FakeEvaluator Throws(string s) => new(s, (_, _) => throw new InvalidOperationException($"{s} boom"));

    /// <summary>An <see cref="IEval"/> that returns a fixed tree (no LLM) — populates CapturedResults cheaply.</summary>
    public sealed class StubComposite(EvalResult tree) : IEval
    {
        public string Key => "stub";
        public string Name => "StubComposite";
        public string Category => "agentic";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(tree);
    }

    public static EvalResult Leaf(string name, double value) => new(
        new EvalMetadata(name, name, "quality", "1.0.0"),
        new EvalScore(value, null, value >= 0.7 ? "pass" : "fail", value >= 0.7, 0.7, value >= 0.7 ? "none" : "high", null),
        new EvalDetails(null, null, null, null, null),
        new EvalProvenance("atomic-llm", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);

    public static EvalResult Tree(params EvalResult[] subs) => new(
        new EvalMetadata("root", "StubComposite", "agentic", "1.0.0"),
        new EvalScore(subs.Length == 0 ? 0 : subs.Average(s => s.Score.Value), null, "pass", true, 0.7, "none", null),
        new EvalDetails(null, null, null, subs, "mean"),
        new EvalProvenance("composite", null, null, null, null, 0, false),
        DateTimeOffset.UtcNow);
}
