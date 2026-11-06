// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Evaluators;
using AgentEval.Testing;   // ScriptedChatClient, FakeChatClient
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Runs MAF's own <c>agent.EvaluateAsync</c> on a real <see cref="ChatClientAgent"/> over a scripted client whose turn
/// makes one tool call and then answers (no network, no judge call). Pins the table in
/// docs/using-agenteval-with-maf-evals.md ("Why this changes tool-metric results") and the matching sentence in
/// docs/llm-as-judge.md on the MAF version this repository references.
/// </summary>
/// <remarks>
/// MAF's MEAI <c>IEvaluator</c> overload wraps the evaluator in its internal <c>MeaiEvaluatorAdapter</c>, which sends
/// the split's query messages and <c>EvalItem.RawResponse</c> (built from the last returned message), while
/// <c>EvalItem.Conversation</c> keeps the tool turns. AgentEval's <c>AsAgentEvaluator</c> forwards the conversation.
/// MAF's evaluation source is the same at 1.24.0 and 1.27.0. If a later MAF changes the projection, these tests fail
/// and the docs table needs updating.
/// </remarks>
public sealed class MAFEvaluateAsyncProjectionTests
{
    private const string Query = "What is the weather in Zurich?";
    private const string FinalReply = "It is sunny in Zurich.";

    [Theory]
    [InlineData(false)]   // default splitter (LastTurn)
    [InlineData(true)]    // ConversationSplitters.Full
    public async Task MeaiEvaluatorOverload_SendsQueryAndFinalReplyOnly_WhileTheItemKeepsTheToolTurns(bool fullSplitter)
    {
        var (agent, executed) = BuildWeatherAgent();
        var capturing = new CapturingEvaluator();

        var results = await agent.EvaluateAsync(
            new[] { Query },
            capturing,
            new ChatConfiguration(new FakeChatClient("judge")),
            splitter: fullSplitter ? ConversationSplitters.Full : null);

        Assert.Equal(1, executed());   // the tool ran inside the agent's turn

        var (messages, response) = Assert.Single(capturing.Calls);
        var query = Assert.Single(messages);
        Assert.Equal(ChatRole.User, query.Role);
        Assert.Equal(Query, query.Text);
        var reply = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, reply.Role);
        Assert.Equal(FinalReply, reply.Text);
        Assert.DoesNotContain(messages.Concat(response.Messages).SelectMany(m => m.Contents),
            c => c is FunctionCallContent or FunctionResultContent);

        // MAF's orchestration still holds the whole transcript on the item it built.
        var item = Assert.Single(results.InputItems!);
        var transcript = item.Conversation.SelectMany(m => m.Contents).ToList();
        Assert.Contains(transcript, c => c is FunctionCallContent { CallId: "call_1", Name: "get_weather" });
        Assert.Contains(transcript, c => c is FunctionResultContent { CallId: "call_1" });
    }

    [Fact]
    public async Task AsAgentEvaluator_NativeRoute_ForwardsTheToolTurns()
    {
        var (agent, executed) = BuildWeatherAgent();
        var capturing = new CapturingEvaluator();
        var chatConfiguration = new ChatConfiguration(new FakeChatClient("judge"));

        var results = await agent.EvaluateAsync(new[] { Query }, capturing.AsAgentEvaluator(chatConfiguration));

        Assert.Equal(1, executed());

        var (messages, response) = Assert.Single(capturing.Calls);
        var item = Assert.Single(results.InputItems!);
        Assert.Equal(item.Conversation.ToList(), messages);   // the full captured conversation, unchanged
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(Query, messages[0].Text);
        var contents = messages.SelectMany(m => m.Contents).ToList();
        Assert.Contains(contents, c => c is FunctionCallContent { CallId: "call_1", Name: "get_weather" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call_1" });
        Assert.Equal(FinalReply, messages[^1].Text);
        Assert.Equal(FinalReply, Assert.Single(response.Messages).Text);
    }

    private static (AIAgent Agent, Func<int> Executed) BuildWeatherAgent()
    {
        var executed = 0;
        var weather = AIFunctionFactory.Create(
            (string city) => { Interlocked.Increment(ref executed); return $"sunny in {city}"; }, "get_weather");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Zurich" })
            .AddText(FinalReply);
        return (new ChatClientAgent(scripted, name: "Weather", tools: [weather]), () => executed);
    }

    /// <summary>An MEAI evaluator that records both arguments it is handed: the messages and the response.</summary>
    private sealed class CapturingEvaluator : Microsoft.Extensions.AI.Evaluation.IEvaluator
    {
        public List<(List<ChatMessage> Messages, ChatResponse Response)> Calls { get; } = [];

        public IReadOnlyCollection<string> EvaluationMetricNames => ["captured"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse modelResponse,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((messages.ToList(), modelResponse));
            var result = new EvaluationResult();
            result.Metrics["captured"] = new NumericMetric("captured", 5.0, "ok");
            return ValueTask.FromResult(result);
        }
    }
}
