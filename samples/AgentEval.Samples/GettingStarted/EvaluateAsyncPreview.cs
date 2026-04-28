// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// ═══════════════════════════════════════════════════════════════════════════════
// PREVIEW: agent.EvaluateAsync() extension method
//
// MAF's ADR-0020 defines AgentEvaluationExtensions.EvaluateAsync() as extension
// methods on AIAgent. That API is not yet shipped in Microsoft.Agents.AI.
//
// This preview implementation provides the same developer experience today.
// When MAF ships the official version, swap this using for theirs — the call
// sites remain identical.
//
// This file lives in SAMPLES, not in the AgentEval production package.
// ═══════════════════════════════════════════════════════════════════════════════

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.Samples;

/// <summary>
/// Preview of MAF's <c>agent.EvaluateAsync()</c> from ADR-0020.
/// Runs the agent per query, passes conversation to MEAI evaluator, aggregates results.
/// </summary>
public static class EvaluateAsyncPreview
{
    /// <summary>
    /// Evaluates an agent against test queries using a single MEAI evaluator.
    /// </summary>
    public static async Task<AgentEvalResults> EvaluateAsync(
        this AIAgent agent,
        IEnumerable<string> queries,
        MEAIIEvaluator evaluator,
        CancellationToken cancellationToken = default)
    {
        var items = new List<AgentEvalResultItem>();

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (agentResponse, messages, response) = await RunQueryAsync(agent, query, cancellationToken);

            var evalResult = await evaluator.EvaluateAsync(
                messages, response, cancellationToken: cancellationToken);

            items.Add(new AgentEvalResultItem(query, agentResponse.Text, evalResult));
        }

        return new AgentEvalResults(items);
    }

    /// <summary>
    /// Evaluates an agent against test queries using multiple evaluators.
    /// Returns one result set per evaluator.
    /// </summary>
    public static async Task<IReadOnlyList<AgentEvalResults>> EvaluateAsync(
        this AIAgent agent,
        IEnumerable<string> queries,
        IEnumerable<MEAIIEvaluator> evaluators,
        CancellationToken cancellationToken = default)
    {
        // Run agent once per query, collect conversations
        var conversations = new List<(string Query, string ResponseText, List<ChatMessage> Messages, ChatResponse Response)>();
        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (agentResponse, messages, response) = await RunQueryAsync(agent, query, cancellationToken);
            conversations.Add((query, agentResponse.Text, messages, response));
        }

        // Run each evaluator across all conversations
        var results = new List<AgentEvalResults>();
        foreach (var evaluator in evaluators)
        {
            var items = new List<AgentEvalResultItem>();
            foreach (var (query, responseText, messages, response) in conversations)
            {
                var evalResult = await evaluator.EvaluateAsync(
                    messages, response, cancellationToken: cancellationToken);
                items.Add(new AgentEvalResultItem(query, responseText, evalResult));
            }
            results.Add(new AgentEvalResults(items));
        }

        return results;
    }

    /// <summary>
    /// Runs the agent for a single query and returns the response plus the formatted conversation.
    /// Disposes the session when done if it implements <see cref="IAsyncDisposable"/>.
    /// </summary>
    private static async Task<(AgentResponse Response, List<ChatMessage> Messages, ChatResponse Chat)>
        RunQueryAsync(AIAgent agent, string query, CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken);
        // Dispose the session when done if it supports async disposal.
        // This is a no-op for session types that are not IAsyncDisposable.
        await using (session as IAsyncDisposable)
        {
            var agentResponse = await agent.RunAsync(query, session, cancellationToken: cancellationToken);

            var messages = new List<ChatMessage> { new(ChatRole.User, query) };
            foreach (var msg in agentResponse.Messages)
                messages.Add(msg);
            var chat = new ChatResponse([new ChatMessage(ChatRole.Assistant, agentResponse.Text)]);

            return (agentResponse, messages, chat);
        }
    }
}

/// <summary>
/// Aggregated evaluation results. Mirrors MAF's AgentEvaluationResults from ADR-0020.
/// </summary>
public class AgentEvalResults
{
    public AgentEvalResults(IReadOnlyList<AgentEvalResultItem> items) => Items = items;

    public IReadOnlyList<AgentEvalResultItem> Items { get; }
    public int Passed => Items.Count(i => i.AllPassed);
    public int Failed => Items.Count(i => !i.AllPassed);
    public int Total => Items.Count;
    public bool AllPassed => Items.All(i => i.AllPassed);

    public void AssertAllPassed(string? message = null)
    {
        if (AllPassed) return;

        var failures = Items
            .Where(i => !i.AllPassed)
            .Select(i =>
            {
                var failedMetrics = i.Metrics
                    .Where(m => m.Value is NumericMetric nm && nm.Interpretation?.Failed == true)
                    .Select(m => m.Key);
                return $"  \"{Truncate(i.Query, 60)}\": failed [{string.Join(", ", failedMetrics)}]";
            });

        throw new InvalidOperationException(
            message ?? $"Evaluation failed: {Failed}/{Total} queries did not pass.\n{string.Join("\n", failures)}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Evaluation result for a single query.
/// </summary>
public class AgentEvalResultItem
{
    public AgentEvalResultItem(string query, string response, MEAIEvaluationResult evaluationResult)
    {
        Query = query;
        Response = response;
        EvaluationResult = evaluationResult;
    }

    public string Query { get; }
    public string Response { get; }
    public MEAIEvaluationResult EvaluationResult { get; }
    public IDictionary<string, EvaluationMetric> Metrics => EvaluationResult.Metrics;

    public bool AllPassed => EvaluationResult.Metrics.Values
        .OfType<NumericMetric>()
        .All(m => m.Interpretation?.Failed != true);
}
