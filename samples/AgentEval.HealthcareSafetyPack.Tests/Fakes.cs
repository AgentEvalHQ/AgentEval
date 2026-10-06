// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.HealthcareSafetyPack.Tests;

/// <summary>One question a check sent to the judge.</summary>
public sealed record JudgeCall(string Input, string Output, IReadOnlyList<string> Criteria)
{
    /// <summary>Which check asked, read from its criteria.</summary>
    public string Check => Criteria[0] switch
    {
        var c when c.Contains("emergency care", StringComparison.Ordinal) => CheckKeys.Escalation,
        var c when c.Contains("safe dose", StringComparison.Ordinal) => CheckKeys.MedicationSafety,
        var c when c.Contains("research, trials or guidelines", StringComparison.Ordinal) => CheckKeys.SourceSupport,
        var c when c.Contains("makes a decision", StringComparison.Ordinal) => CheckKeys.AuditTrail,
        var c => throw new InvalidOperationException($"No check asks: {c}"),
    };
}

/// <summary>
/// A judge that answers from a script and records every question. It stands in for the model so the tests can check
/// what each check sends and how the pack reads the answer; how well a real judge grades is what --calibrate measures.
/// </summary>
public sealed class ScriptedJudge(Func<JudgeCall, EvaluationResult> script) : IEvaluator
{
    /// <summary>Every question asked, in the order the checks asked them.</summary>
    public ConcurrentQueue<JudgeCall> Calls { get; } = new();

    /// <summary>A judge that passes everything.</summary>
    public static ScriptedJudge PassingAll() => new(_ => Pass);

    public static EvaluationResult Pass => new() { OverallScore = 90, Summary = "Met." };

    public static EvaluationResult Fail => new() { OverallScore = 10, Summary = "Not met." };

    public static EvaluationResult NoVerdict => new() { OverallScore = 0, Summary = "Unparseable.", EvaluationFailed = true };

    public Task<EvaluationResult> EvaluateAsync(
        string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
    {
        var call = new JudgeCall(input, output, criteria.ToList());
        Calls.Enqueue(call);
        return Task.FromResult(script(call));
    }
}

/// <summary>
/// A model that answers from a script, one turn per request, and records what it was sent. Wrapped with the real
/// <c>UseFunctionInvocation</c>, a scripted function call runs the sample's recording tools.
/// </summary>
public sealed class ScriptedModel(params Func<ChatResponse>[] turns) : IChatClient
{
    private int _turn;

    /// <summary>The messages of every request, in order.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    /// <summary>The options of the last request.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>A turn that calls <paramref name="tool"/>.</summary>
    public static Func<ChatResponse> Calls(string tool) => () => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent($"call-{tool}", tool, new Dictionary<string, object?> { ["details"] = "as requested" })]));

    /// <summary>A turn that answers <paramref name="text"/>.</summary>
    public static Func<ChatResponse> Says(string text) => () => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>A turn that throws <paramref name="exception"/>.</summary>
    public static Func<ChatResponse> Throws(Exception exception) => () => throw exception;

    /// <summary>The agent the sample would use: this model behind the real function-invoking client.</summary>
    public IChatClient AsAgent() => new ChatClientBuilder(this).UseFunctionInvocation().Build();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        LastOptions = options;
        var turn = turns[Math.Min(_turn++, turns.Length - 1)];
        return Task.FromResult(turn());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The sample does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>The shipped fixtures, loaded once.</summary>
public static class Fixtures
{
    private static readonly Lazy<(IReadOnlyList<HealthcareScenario> Scenarios, IReadOnlyList<GoldLabel> Gold)> s_data = new(() =>
    {
        var dir = HealthcareSafetyData.ResolveDataDirectory();
        var scenarios = HealthcareSafetyData.LoadJsonlAsync<HealthcareScenario>(Path.Combine(dir, "scenarios.jsonl")).GetAwaiter().GetResult();
        var gold = HealthcareSafetyData.LoadJsonlAsync<GoldLabel>(Path.Combine(dir, "gold.jsonl")).GetAwaiter().GetResult();
        return (scenarios, gold);
    });

    public static IReadOnlyList<HealthcareScenario> Scenarios => s_data.Value.Scenarios;

    public static IReadOnlyList<GoldLabel> Gold => s_data.Value.Gold;

    public static HealthcareScenario Scenario(string id) => Scenarios.Single(s => s.ScenarioId == id);

    public static GoldLabel Label(string id) => Gold.Single(g => g.ScenarioId == id);
}
