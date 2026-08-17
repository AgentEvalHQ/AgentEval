// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The two guards that make ADR-026's promises regression tests rather than review comments:
/// a TypedMemEval result must be unquotable as LongMemEval, and the corpus's answer key must never
/// reach the agent under test.
/// </summary>
public sealed class TypedMemEvalGuardTests
{
    [Fact]
    public async Task RunResult_ContainsNoLongMemEvalToken()
    {
        // Rule §2.3. The family reuses LongMemEval's file format and machinery, which is an
        // engineering fact and is fine; what must never happen is a family result carrying the
        // other benchmark's name into a report, a spreadsheet, or a paper.
        var result = await RunAsync(TypedMemEvalVertical.Prospective, maxQuestions: 4);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });

        Assert.DoesNotContain("longmemeval", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("typedmemeval", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunResult_IdentifiesItsCorpusAndRevision()
    {
        // Rule §2.1. A result that cannot say which corpus produced it is exactly the artefact the
        // identity rule exists to prevent, so provenance is forced to Full rather than merely
        // non-None: the dataset identifier and hash are populated only under Full.
        var result = await RunAsync(TypedMemEvalVertical.Forgetting, maxQuestions: 3);

        Assert.Equal("typedmemeval-forgetting", result.BenchmarkId);
        Assert.StartsWith(
            $"TypedMemEval-Forgetting {TypedMemEvalVerticalDescriptor.CorpusRevision}",
            result.BenchmarkName, StringComparison.Ordinal);
        Assert.NotNull(result.Provenance);
        Assert.Equal(RunProvenanceMode.Full, result.Provenance!.Mode);
        Assert.Equal("agenteval-typedmemeval-forgetting-v5", result.Provenance.DatasetIdentifier);
        Assert.Equal(
            TypedMemEvalCorpus.Sha256(TypedMemEvalVertical.Forgetting), // DevSkim: ignore DS197836
            result.Provenance.DatasetSha256);
        Assert.Null(result.Provenance.DatasetPath);

        // The family's own judge fingerprint, not the frozen LongMemEval one. These must differ,
        // or judge drift in one benchmark would be invisible in the other.
        Assert.Equal(TypedMemEvalJudge.PromptFingerprint, result.Provenance.JudgePromptFingerprint);
        Assert.Equal(TypedMemEvalJudge.PromptFingerprint, result.TypedOutcomes!.JudgePromptFingerprint);
    }

    [Fact]
    public void EntryModel_CannotCarryTheAnswerKey()
    {
        // The structural half of the leak guard. The corpus's typedmemeval block holds gold
        // derivations, component indices and pair arms; LongMemEvalEntry has no member for it, so
        // the entry the history formatter reads cannot carry it even by accident.
        var raw = TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Arithmetic);
        Assert.Contains("\"typedmemeval\"", raw, StringComparison.Ordinal);

        var entries = TypedMemEvalCorpus.Load(TypedMemEvalVertical.Arithmetic);
        var roundTripped = JsonSerializer.Serialize(entries);

        Assert.DoesNotContain("typedmemeval", roundTripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("derivation", roundTripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gold_components", roundTripped, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TypedMemEvalVertical.Prospective)]
    [InlineData(TypedMemEvalVertical.Episodic)]
    [InlineData(TypedMemEvalVertical.Arithmetic)]
    [InlineData(TypedMemEvalVertical.WorkingMemory)]
    [InlineData(TypedMemEvalVertical.Forgetting)]
    public void AssembledPrompts_NeverContainTheExtensionBlock(TypedMemEvalVertical vertical)
    {
        // The behavioural half. One formatter bug that passed raw question objects through would
        // inject the answer key into the prompt and silently invalidate every number the corpus
        // ever produces, so the assertion is over the text an agent would actually receive.
        var options = new ExternalBenchmarkOptions
        {
            HistoryInjectionMode = HistoryInjectionMode.TextBlob
        };

        foreach (var entry in TypedMemEvalCorpus.Load(vertical))
        {
            var blob = LongMemEvalHistoryFormatter.FormatAsTextBlob(entry, options);
            var prompt = $"{blob}\nQuestion: {entry.Question}\nAnswer:";

            Assert.DoesNotContain("typedmemeval", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gold_components", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("session_index", prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ArithmeticPrompts_NeverStateTheDerivedValue()
    {
        // Scoped where a literal match is actually diagnostic. A count's gold is a small integer
        // whose appearance in a twenty-session haystack is easily coincidental, so counts rely on
        // the block assertion above; sums, deltas and durations carry values whose literal presence
        // could only come from a leak, because V5 derives them and never states them.
        var extensions = TypedMemEvalExtensions.Parse(
            TypedMemEvalCorpus.ReadJson(TypedMemEvalVertical.Arithmetic));
        var options = new ExternalBenchmarkOptions
        {
            HistoryInjectionMode = HistoryInjectionMode.TextBlob
        };

        var checkedQuestions = 0;
        foreach (var entry in TypedMemEvalCorpus.Load(TypedMemEvalVertical.Arithmetic))
        {
            var derivation = extensions[entry.QuestionId].Derivation!;
            if (derivation.Operation == "count" || Math.Abs(derivation.Value) < 100)
                continue;

            var blob = LongMemEvalHistoryFormatter.FormatAsTextBlob(entry, options);
            var rendered = derivation.Value.ToString("0.##", CultureInfo.InvariantCulture);

            Assert.DoesNotContain(rendered, blob, StringComparison.Ordinal);
            checkedQuestions++;
        }

        // A guard that silently checked nothing would be worse than no guard.
        Assert.True(checkedQuestions >= 10, $"only {checkedQuestions} questions were diagnosable");
    }

    [Fact]
    public async Task Run_RefusesWhenTheAgentCannotReceiveTimestamps()
    {
        // Fail before measuring. Falling back to text injection would answer the vertical's
        // questions from the very date scaffolding the mode exists to remove, and would report a
        // score for a measurement that never happened.
        var runner = new TypedMemEvalRunner(new VerdictChatClient("correct"));
        var agent = new BlindAgent();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync(agent, TypedMemEvalVertical.Prospective));

        Assert.Equal("agent", error.ParamName);
        Assert.Contains("ITimestampedHistoryInjectableAgent", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, agent.Invocations);
    }

    internal static async Task<ExternalBenchmarkResult> RunAsync(
        TypedMemEvalVertical vertical,
        int maxQuestions,
        string verdict = "correct")
    {
        var runner = new TypedMemEvalRunner(new VerdictChatClient(verdict));
        return await runner.RunAsync(
            new RecordingAgent(),
            vertical,
            new TypedMemEvalOptions { MaxQuestions = maxQuestions, RandomSeed = 7 });
    }

    /// <summary>An agent that can receive everything the family injects and records what it saw.</summary>
    internal sealed class RecordingAgent
        : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent, ISessionResettableAgent
    {
        public string Name => "recording";

        public List<string> Prompts { get; } = [];

        public int Resets { get; private set; }

        public void InjectConversationHistory(
            IEnumerable<(string UserMessage, string AssistantResponse)> history)
        {
        }

        public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
        {
        }

        public Task ResetSessionAsync(CancellationToken cancellationToken = default)
        {
            Resets++;
            return Task.CompletedTask;
        }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AgentResponse { Text = "an answer" });
        }
    }

    /// <summary>An agent with no capability interfaces at all.</summary>
    private sealed class BlindAgent : IEvaluableAgent
    {
        public string Name => "blind";

        public int Invocations { get; private set; }

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(new AgentResponse { Text = "an answer" });
        }
    }

    /// <summary>A judge client that always returns the same structured verdict.</summary>
    internal sealed class VerdictChatClient(
        string outcome,
        bool staleValueAsserted = false,
        bool claimedNoLongerKnown = false) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var payload =
                $$"""
                {"outcome": "{{outcome}}", "reasoning": "fixed test verdict",
                 "stale_value_asserted": {{(staleValueAsserted ? "true" : "false")}},
                 "claimed_no_longer_known": {{(claimedNoLongerKnown ? "true" : "false")}},
                 "ordered_pairs_correct": 3, "ordered_pairs_total": 3}
                """;
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
}
