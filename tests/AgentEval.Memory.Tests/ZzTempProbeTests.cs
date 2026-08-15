// SPDX-License-Identifier: MIT
// THROWAWAY REVIEW PROBE — delete after use.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Memory.Tests;

public sealed class ZzTempProbeTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- A
    [Fact]
    public async Task Probe_TimeBlindSystem_PairConsistency()
    {
        var runner = new TypedMemEvalRunner(new GoldAwareJudge());
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Prospective);

        var pairs = result.TypedOutcomes!.PairConsistency!;
        output.WriteLine($"pairs={pairs.Pairs} bothCorrect={pairs.BothArmsCorrect} " +
                         $"premature={pairs.PrematureBefore} missedAfter={pairs.MissedAfter} " +
                         $"bothSame={pairs.BothArmsSameOutcome}");
    }

    /// <summary>
    /// Simulates a system that never received the query time and always answers "not yet":
    /// correct on every before-arm, a confident denial on every after-arm.
    /// </summary>
    private sealed class GoldAwareJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", chatMessages.Select(m => m.Text));
            var goldIndex = prompt.IndexOf("GOLD ANSWER", StringComparison.Ordinal);
            var gold = goldIndex >= 0 ? prompt[goldIndex..] : prompt;
            var outcome = gold.Contains("Not yet", StringComparison.OrdinalIgnoreCase)
                ? "correct"
                : "missed";
            var payload = $$"""{"outcome": "{{outcome}}", "reasoning": "probe"}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, payload)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- E
    [Fact]
    public async Task Probe_JudgeOutage_PairConsistency()
    {
        var runner = new TypedMemEvalRunner(new GarbageJudge());
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Prospective);

        var typed = result.TypedOutcomes!;
        var pairs = typed.PairConsistency!;
        output.WriteLine($"inconclusive={typed.Outcomes.Inconclusive} pairs={pairs.Pairs} " +
                         $"bothCorrect={pairs.BothArmsCorrect} bothSame={pairs.BothArmsSameOutcome}");
    }

    private sealed class GarbageJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- B
    [Fact]
    public async Task Probe_AttributionLevel_WhenNoGoldWasEverReferenced()
    {
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Forgetting));
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var agent = new WrongSessionEvidenceAgent(extensions);

        var result = await runner.RunAsync(
            agent,
            TypedMemEvalVertical.Forgetting,
            new TypedMemEvalOptions { EvidenceCaptureMode = EvidenceCaptureMode.References });

        var typed = result.TypedOutcomes!;
        output.WriteLine($"run level = {typed.Attribution.Level}");
        output.WriteLine($"coverage mean = {typed.Coverage.Mean}");
        var perQuestion = result.QuestionResults
            .Select(q => q.TypedOutcome!)
            .GroupBy(d => d.AttributionLevel)
            .Select(g => $"{g.Key}={g.Count()}");
        output.WriteLine("levels: " + string.Join(", ", perQuestion));
    }

    /// <summary>Surfaces references that name only NON-gold sessions, with no content.</summary>
    private sealed class WrongSessionEvidenceAgent(
        IReadOnlyDictionary<string, TypedMemEvalExtension> extensions)
        : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent, ISessionResettableAgent
    {
        private int _index;

        public string Name => "wrong-session";

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> history)
        {
        }

        public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
        {
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var extension = extensions.Values.ElementAt(_index++);
            var gold = extension.GoldSessionIndices.ToHashSet();
            var distractors = Enumerable.Range(0, extension.SessionIds.Count)
                .Where(i => !gold.Contains(i))
                .Take(3)
                .Select((sessionIndex, rank) => new EvidenceReference
                {
                    Id = $"ref-{rank}",
                    Rank = rank + 1,
                    SourceSessionId = extension.SessionIds[sessionIndex],
                    AnswerContextOrder = rank + 1
                })
                .ToArray();

            var envelope = new QuestionEvidenceEnvelope
            {
                SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
                Retrieved = distractors,
                AnswerContext = distractors
            };

            return Task.FromResult(new AgentResponse
            {
                Text = "an answer",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [QuestionEvidenceEnvelope.AdditionalPropertiesKey] = JsonSerializer.Serialize(envelope)
                }
            });
        }
    }

    // ---------------------------------------------------------------- C
    [Fact]
    public async Task Probe_UnrunQuestionsCarryNoTypedOutcome()
    {
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Arithmetic,
            new TypedMemEvalOptions { IncludeTimestamps = false });

        var nullDetails = result.QuestionResults.Count(q => q.TypedOutcome is null);
        output.WriteLine($"report Unrun = {result.TypedOutcomes!.Outcomes.Unrun}, " +
                         $"QuestionResults with null TypedOutcome = {nullDetails}");
        var json = JsonSerializer.Serialize(result);
        output.WriteLine($"serialized length {json.Length}");
    }

    // ---------------------------------------------------------------- D
    [Fact]
    public async Task Probe_RunSetBandsAcrossDifferentTimestampExposure()
    {
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var withDates = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Arithmetic,
            new TypedMemEvalOptions { IncludeTimestamps = true });
        var withoutDates = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Arithmetic,
            new TypedMemEvalOptions { IncludeTimestamps = false });

        var summary = TypedMemEvalRunSet.Summarize([withDates, withoutDates]);
        output.WriteLine($"BANDED across different IncludeTimestamps: compared={summary.QuestionsCompared} " +
                         $"flips={summary.QuestionsWithFlips} " +
                         $"correct band={summary.Outcomes[TypedMemEvalOutcome.Correct].Minimum}" +
                         $"-{summary.Outcomes[TypedMemEvalOutcome.Correct].Maximum}");
    }
}
