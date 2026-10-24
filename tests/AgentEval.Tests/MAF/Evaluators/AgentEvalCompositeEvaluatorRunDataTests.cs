// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// What a composite run through MAF's <c>agent.EvaluateAsync(...)</c> is given. <see cref="AgentEvalCompositeEvaluator"/>
/// built its <see cref="EvalInput"/> from the query, the response, the reference and the context only: the run's tool
/// calls, the agent's tools, the case's expected tools and the agent's instructions were on the item and never reached
/// it, so a preset such as <c>AgenticBenchmark.ToolCallAccuracy</c> judged tool use from the reply text. And
/// <see cref="AgentEvalAgentEvaluator"/> never forwarded <c>EvalItem.Tools</c> or <c>EvalItem.ExpectedToolCalls</c>, so
/// M.E.AI's own tool-call-accuracy evaluator had no tool definitions either.
/// </summary>
public class AgentEvalCompositeEvaluatorRunDataTests
{
    /// <summary>An eval that records the input it was given.</summary>
    private sealed class CapturingEval : IEval
    {
        public EvalInput? Seen { get; private set; }
        public string Key => "capture";
        public string Name => "Capture";
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            Seen = input;
            return Task.FromResult(EvalResult.Skipped(this, "captured"));
        }
    }

    /// <summary>An MEAI evaluator that records the messages and the contexts it was handed.</summary>
    private sealed class CapturingMeaiEvaluator : Microsoft.Extensions.AI.Evaluation.IEvaluator
    {
        public List<ChatMessage>? Messages { get; private set; }
        public List<EvaluationContext> Contexts { get; } = [];

        public IReadOnlyCollection<string> EvaluationMetricNames => ["captured"];

        public ValueTask<EvaluationResult> EvaluateAsync(
            IEnumerable<ChatMessage> messages,
            ChatResponse response,
            ChatConfiguration? chatConfiguration = null,
            IEnumerable<EvaluationContext>? additionalContext = null,
            CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Contexts.AddRange(additionalContext ?? []);
            var result = new EvaluationResult();
            result.Metrics["captured"] = new NumericMetric("captured", 5.0, "ok");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Records what each AgentEval judge call was sent. Its reply is never asserted on.</summary>
    private sealed class InputRecordingJudge : AgentEval.Core.IEvaluator
    {
        public List<string> Inputs { get; } = [];

        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Inputs.Add(input);
            return Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 42, Summary = "recording-stub" });
        }
    }

    private static ChatConfiguration Judge() => new(new AgentEval.Testing.FakeChatClient("judge"));

    private static AIFunction GetWeather() => AIFunctionFactory.Create((string city) => "18C", "get_weather", "Gets the weather.");

    // A turn that called get_weather, as MAF records it: the call, its result, then the answer.
    private static EvalItem WeatherItem() => new("Weather in Paris?", "It is 18C",
    [
        new ChatMessage(ChatRole.User, "Weather in Paris?"),
        new ChatMessage(ChatRole.Assistant, new AIContent[] { new FunctionCallContent("c1", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris" }) }),
        new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent("c1", "18C") }),
        new ChatMessage(ChatRole.Assistant, "It is 18C"),
    ]);

    private static async Task<EvalInput> SeenOnTheNativePath(EvalItem item, string? agentInstructions = null)
    {
        var eval = new CapturingEval();
        await new AgentEvalAgentEvaluator(new AgentEvalCompositeEvaluator(eval), Judge()) { AgentInstructions = agentInstructions }
            .EvaluateAsync([item]);
        return eval.Seen!;
    }

    // ── The run's tool calls ─────────────────────────────────────────────────

    [Fact]
    public async Task TheTurnsToolCalls_ReachTheComposite_WithTheirRecordedOutcome()
    {
        var seen = await SeenOnTheNativePath(WeatherItem());

        var call = Assert.Single(seen.ToolCalls!);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", call.Arguments!["city"]);
        Assert.Equal("18C", call.Result);
        Assert.True(call.Succeeded);
        Assert.Null(call.Error);
    }

    [Fact]
    public async Task ATurnWithNoToolCall_IsAMeasuredNone_OnTheNativePath()
    {
        // The adapter forwards the item's whole conversation, so no call in it is an observation, not an absence.
        var seen = await SeenOnTheNativePath(new EvalItem("What is the capital of France?", "Paris."));

        Assert.NotNull(seen.ToolCalls);
        Assert.Empty(seen.ToolCalls);
    }

    [Fact]
    public async Task APerTurnSlice_CountsOnlyItsLastTurnsCalls()
    {
        // EvalItem.PerTurnItems hands each turn the conversation up to it; an earlier turn's call is not this turn's.
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, "Who am I?"),
            new(ChatRole.Assistant, new AIContent[] { new FunctionCallContent("c0", "lookup_user") }),
            new(ChatRole.Tool, new AIContent[] { new FunctionResultContent("c0", "Ada") }),
            new(ChatRole.Assistant, "You are Ada."),
            new(ChatRole.User, "Thanks."),
            new(ChatRole.Assistant, "You're welcome."),
        };

        var seen = await SeenOnTheNativePath(new EvalItem("Thanks.", "You're welcome.", conversation));

        Assert.NotNull(seen.ToolCalls);
        Assert.Empty(seen.ToolCalls);
    }

    [Fact]
    public async Task AnApprovalGatedCall_IsACall_RecordedAsNotRun()
    {
        // MAF wraps an approval-required call in a ToolApprovalRequestContent; read as no call, a NeverCallTool-style
        // check would pass on the very call it forbids.
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, "Delete the customers table."),
            new(ChatRole.Assistant, new AIContent[] { new ToolApprovalRequestContent("r1", new FunctionCallContent("c1", "delete_records")) }),
        };

        var seen = await SeenOnTheNativePath(new EvalItem("Delete the customers table.", "", conversation));

        var call = Assert.Single(seen.ToolCalls!);
        Assert.Equal("delete_records", call.Name);
        Assert.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, call.Result, StringComparison.Ordinal);
        Assert.Null(call.Succeeded);
    }

    [Fact]
    public async Task OnADirectMeaiCall_TheCallsInTheConversation_ReachTheComposite()
    {
        var eval = new CapturingEval();
        var item = WeatherItem();

        await new AgentEvalCompositeEvaluator(eval).EvaluateAsync(item.Conversation, new ChatResponse(item.Conversation[^1]));

        Assert.Equal("get_weather", Assert.Single(eval.Seen!.ToolCalls!).Name);
    }

    [Fact]
    public async Task OnADirectMeaiCall_AQueryHalfWithNoCall_IsUnknown_NotANone()
    {
        // MAF's own MEAI adapter sends the query half and the last message: no call in them says nothing about the run.
        var eval = new CapturingEval();

        await new AgentEvalCompositeEvaluator(eval).EvaluateAsync(
            [new ChatMessage(ChatRole.User, "Weather in Paris?")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "It is 18C")));

        Assert.Null(eval.Seen!.ToolCalls);
    }

    // ── The agent's tools and the case's expected tools ──────────────────────

    [Fact]
    public async Task TheItemsTools_ReachTheComposite_AsToolDefinitions()
    {
        var item = WeatherItem();
        item.Tools = [GetWeather()];

        var seen = await SeenOnTheNativePath(item);

        var tool = Assert.Single(seen.ToolDefinitions!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Gets the weather.", tool.Description);
        Assert.True(tool.Parameters!.ContainsKey("properties"));
    }

    [Fact]
    public async Task TheItemsToolsAndExpectedToolCalls_ReachMeaisOwnEvaluatorsToo()
    {
        var capturing = new CapturingMeaiEvaluator();
        var item = WeatherItem();
        item.Tools = [GetWeather()];
        item.ExpectedToolCalls = [new ExpectedToolCall("get_weather")];

        await new AgentEvalAgentEvaluator(capturing, Judge()).EvaluateAsync([item]);

#pragma warning disable AIEVAL001   // M.E.AI marks its tool evaluators' contexts experimental
        var accuracy = Assert.Single(capturing.Contexts.OfType<Microsoft.Extensions.AI.Evaluation.Quality.ToolCallAccuracyEvaluatorContext>());
        Assert.Equal("get_weather", Assert.Single(accuracy.ToolDefinitions).Name);
        Assert.Contains(capturing.Contexts, c => c is Microsoft.Extensions.AI.Evaluation.Quality.TaskAdherenceEvaluatorContext);
        Assert.Contains(capturing.Contexts, c => c is Microsoft.Extensions.AI.Evaluation.Quality.IntentResolutionEvaluatorContext);
#pragma warning restore AIEVAL001
        var expected = Assert.Single(capturing.Contexts.OfType<AgentEvalExpectedToolsContext>());
        Assert.Equal(new[] { "get_weather" }, expected.ExpectedToolNames);
    }

    [Fact]
    public async Task TheItemsExpectedToolCalls_BecomeExpectedActions_OnePerTool()
    {
        var item = WeatherItem();
        item.ExpectedToolCalls = [new ExpectedToolCall("get_weather"), new ExpectedToolCall("get_forecast")];

        var seen = await SeenOnTheNativePath(item);

        Assert.Equal(new[] { "get_weather", "get_forecast" }, seen.ExpectedActions!.Select(a => a.Description));
        Assert.Equal(new[] { "get_forecast" }, seen.ExpectedActions![1].RequiredTools!);
    }

    [Fact]
    public async Task NoToolsAndNoExpectations_StayUnknown()
    {
        var seen = await SeenOnTheNativePath(new EvalItem("q", "a"));

        Assert.Null(seen.ToolDefinitions);
        Assert.Null(seen.ExpectedActions);
        Assert.Null(seen.SystemMessage);
    }

    // ── The agent's instructions ─────────────────────────────────────────────

    [Fact]
    public async Task TheAgentsInstructions_ReachTheComposite_AsItsSystemMessage()
    {
        var seen = await SeenOnTheNativePath(new EvalItem("q", "a"), agentInstructions: "SYS-1 never quote prices");

        Assert.Equal("SYS-1 never quote prices", seen.SystemMessage);
    }

    [Fact]
    public async Task TheInstructions_AsAMafAgentHoldsThem_ReachTheComposite()
    {
        // The lookup AgentInstructions documents: MAF keeps the instructions on the agent's ChatOptions, not on the item.
        AIAgent agent = new ChatClientAgent(new AgentEval.Testing.FakeChatClient("unused"), instructions: "SYS-2 be brief");

        var seen = await SeenOnTheNativePath(new EvalItem("q", "a"), agent.GetService<ChatOptions>()?.Instructions);

        Assert.Equal("SYS-2 be brief", seen.SystemMessage);
    }

    [Fact]
    public async Task TheInstructions_AreTheForwardedConversationsFirstMessage_ForAnyWrappedEvaluator()
    {
        var capturing = new CapturingMeaiEvaluator();

        await new AgentEvalAgentEvaluator(capturing, Judge()) { AgentInstructions = "SYS-3" }.EvaluateAsync([new EvalItem("q", "a")]);

        Assert.Equal(3, capturing.Messages!.Count);
        Assert.Equal(ChatRole.System, capturing.Messages[0].Role);
        Assert.Equal("SYS-3", capturing.Messages[0].Text);
    }

    [Fact]
    public async Task AConversationWithItsOwnSystemMessage_IsForwardedAsItIs()
    {
        var capturing = new CapturingMeaiEvaluator();
        var conversation = new List<ChatMessage> { new(ChatRole.System, "OWN"), new(ChatRole.User, "q"), new(ChatRole.Assistant, "a") };

        await new AgentEvalAgentEvaluator(capturing, Judge()) { AgentInstructions = "SYS-3" }.EvaluateAsync([new EvalItem("q", "a", conversation)]);

        Assert.Equal(conversation, capturing.Messages);
    }

    // ── End to end: what a preset's judge is sent ────────────────────────────

    [Fact]
    public async Task ThroughMaf_TheToolCallAccuracyPresetsJudges_AreShownTheCallsAndTheTools()
    {
        var judge = new InputRecordingJudge();
        var item = WeatherItem();
        item.Tools = [GetWeather()];
        var evaluator = AgentEval.Benchmarks.AgenticBenchmark.ToolCallAccuracy(judge).AsMeaiEvaluator().AsAgentEvaluator(Judge());

        await evaluator.EvaluateAsync([item]);

        Assert.Contains(judge.Inputs, i => i.Contains("Tool calls the agent made, in order", StringComparison.Ordinal)
                                         && i.Contains("\"name\":\"get_weather\"", StringComparison.Ordinal)
                                         && i.Contains("\"result\":\"18C\"", StringComparison.Ordinal));
        Assert.Contains(judge.Inputs, i => i.Contains("Tools the agent was offered", StringComparison.Ordinal)
                                         && i.Contains("Gets the weather.", StringComparison.Ordinal));
    }
}
