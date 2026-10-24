// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Embeddings;
using AgentEval.Evals;
using AgentEval.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Census;

/// <summary>
/// Scripted stand-ins the censuses drive producers with. They supply the inputs a producer reads (a vector, a judge
/// result, a run that threw) and grade nothing: every verdict under test is the producer's own.
/// </summary>
internal static class CensusFakes
{
    // ── Eval scores ──────────────────────────────────────────────────────────

    internal static EvalScore Pass(double value = 1.0) => new(value, null, "pass", true, null, "none", null);

    internal static EvalScore Fail(double value = 0.2, string severity = "high") => new(value, null, "fail", false, null, severity, null);

    internal static EvalScore Error() => new(0, null, "error", false, null, "none", null);

    internal static EvalScore Skipped() => new(0, null, "skipped", false, null, "none", null);

    internal static EvalScore Inapplicable() => EvalScore.NotApplicable();

    /// <summary>A leaf result carrying <paramref name="score"/>.</summary>
    internal static EvalResult Leaf(string key, EvalScore score, string category = "test") => new(
        Metric: new(key, key, category, "1.0.0"),
        Score: score,
        Details: new(null, null, null, null, null),
        Provenance: new("census", null, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>A root result over <paramref name="children"/>, with the root's own <paramref name="score"/>.</summary>
    internal static EvalResult Root(string key, EvalScore score, params EvalResult[] children) => new(
        Metric: new(key, key, "census", "1.0.0"),
        Score: score,
        Details: new(null, null, null, children, "weighted-sum"),
        Provenance: new("composite", null, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UnixEpoch);
}

/// <summary>An eval that answers every call with the same score.</summary>
internal sealed class FixedEval(string key, EvalScore score) : IEval
{
    public string Key => key;
    public string Name => key;
    public string Category => "census";
    public string Version => "1.0.0";

    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        Task.FromResult(CensusFakes.Leaf(key, score, Category));
}

/// <summary>An eval that passes and counts its calls (an adjudicator that must not be asked).</summary>
internal sealed class CountingEval(string key) : IEval
{
    public int Calls { get; private set; }

    public string Key => key;
    public string Name => key;
    public string Category => "census";
    public string Version => "1.0.0";

    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(CensusFakes.Leaf(key, CensusFakes.Pass(), Category));
    }
}

/// <summary>
/// Embeddings scripted per call: each call gets the next reply (the last one repeats), and the caller's token is
/// honoured. Counts the calls, so a census can tell a failed call from one that was never made.
/// </summary>
internal sealed class RecordingEmbeddings(params Func<CancellationToken, IReadOnlyList<ReadOnlyMemory<float>>>[] replies)
    : IAgentEvalEmbeddings
{
    public int Calls { get; private set; }

    /// <summary>Replies with these vectors on every call.</summary>
    internal static RecordingEmbeddings Returning(params float[][] vectors) =>
        new(_ => vectors.Select(v => new ReadOnlyMemory<float>(v)).ToList());

    /// <summary>Throws <paramref name="failure"/> on every call.</summary>
    internal static RecordingEmbeddings Throwing(Exception failure) => new(_ => throw failure);

    public Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the embedding metrics ask for their two vectors in one call");

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var reply = replies[Math.Min(Calls, replies.Length - 1)];
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(reply(cancellationToken));
    }
}

/// <summary>A Microsoft.Extensions.AI embedding generator scripted per call, for the path through <see cref="MEAIEmbeddingAdapter"/>.</summary>
internal sealed class StubEmbeddingGenerator(Func<IReadOnlyList<string>, IReadOnlyList<float[]>> reply)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        var vectors = reply(values.ToList());
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(vectors.Select(v => new Embedding<float>(v))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>An agent whose every call fails, as an unreachable endpoint does.</summary>
internal sealed class ThrowingAgent(string name = "ThrowingAgent") : IEvaluableAgent
{
    public string Name => name;

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
        Task.FromException<AgentResponse>(new HttpRequestException("503 Service Unavailable"));
}

/// <summary>An agent that answers at once, on a priced model, with token usage.</summary>
internal sealed class AnsweringAgent(string name = "AnsweringAgent") : IEvaluableAgent
{
    public string Name => name;

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentResponse
        {
            Text = "ok",
            ModelId = "gpt-4o",
            TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 },
        });
}

/// <summary>An agent the scripted harnesses never call.</summary>
internal sealed class UncalledAgent(string name) : IEvaluableAgent
{
    public string Name => name;

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("the scripted harness never calls the agent");
}

/// <summary>Hands out each agent's scripted run results in order, keyed by the agent's name; the agent is never called.</summary>
internal sealed class ScriptedHarness(Dictionary<string, TestResult[]> runsByAgent) : IEvaluationHarness
{
    private readonly Dictionary<string, int> _next = [];

    public Task<TestResult> RunEvaluationAsync(
        IEvaluableAgent agent, TestCase testCase, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_next)
        {
            var index = _next.GetValueOrDefault(agent.Name);
            _next[agent.Name] = index + 1;
            return Task.FromResult(runsByAgent[agent.Name][index]);
        }
    }
}

/// <summary>Run results as <c>MAFEvaluationHarness</c> writes them.</summary>
internal static class Runs
{
    internal static TestResult Scored(int score, bool passed = true) => new() { TestName = "t", Score = score, Passed = passed };

    /// <summary>A run whose model call threw: Score 0 and Passed false are placeholders.</summary>
    internal static TestResult Errored() => new()
    {
        TestName = "t", Score = 0, Passed = false, Error = new HttpRequestException("429 Too Many Requests"),
    };

    /// <summary>A run whose judge produced no verdict.</summary>
    internal static TestResult JudgeFailed() => new() { TestName = "t", Score = 0, Passed = false, JudgeFailed = true };
}
