// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Realised coverage and evidence attribution, computed from the evidence channel an adapter
/// already supplies.
/// </summary>
/// <remarks>
/// What these assert is mostly what the family refuses to claim: coverage it did not observe,
/// a content-level observation it only partly made, and an attribution for a question whose gold
/// it never had.
/// </remarks>
public sealed class TypedMemEvalCoverageTests
{
    [Fact]
    public async Task Coverage_IsComputedFromTheAnswerContextWhenTheAdapterSuppliesOne()
    {
        var result = await RunWithEvidenceAsync(
            TypedMemEvalVertical.Forgetting,
            useAnswerContext: true,
            goldSessionsToSurface: int.MaxValue);

        var typed = result.TypedOutcomes!;
        Assert.Equal(50, typed.Coverage.Observed);
        Assert.Equal(1.0, typed.Coverage.Mean);
        Assert.Equal(50, typed.Attribution.EvidencePresent);
        Assert.Equal(0, typed.Attribution.Unobserved);
        Assert.Equal(1.0, typed.Attribution.ObservedShare);

        // Scoped to questions that have gold. A never-known probe surfaces no references at all,
        // so no list was used and reporting one would name a list that was never read.
        Assert.All(
            WithGold(result),
            q => Assert.Equal(
                TypedMemEvalCoverageSource.AnswerContext, q.TypedOutcome!.CoverageSource));
    }

    [Fact]
    public async Task Coverage_SaysWhichListItUsedWhenOnlyRetrievalWasSupplied()
    {
        // Retrieval is an upper bound on what the answer model saw. Reporting it silently as
        // answer-context coverage would let a generous adapter and a precise one produce the same
        // number from different evidence.
        var result = await RunWithEvidenceAsync(
            TypedMemEvalVertical.Forgetting,
            useAnswerContext: false,
            goldSessionsToSurface: int.MaxValue);

        Assert.All(
            WithGold(result),
            q => Assert.Equal(
                TypedMemEvalCoverageSource.Retrieved, q.TypedOutcome!.CoverageSource));
    }

    [Fact]
    public async Task Coverage_ReportsPartialRetrievalPerComponent()
    {
        // Forgetting's signature failure is asymmetric retrieval: surfacing the statement but not
        // the invalidation yields a confident stale answer. Only per-component reporting can show
        // it — a single fraction of 0.5 says nothing about *which* half was missed.
        var result = await RunWithEvidenceAsync(
            TypedMemEvalVertical.Forgetting,
            useAnswerContext: true,
            goldSessionsToSurface: 1);

        var twoComponent = result.QuestionResults
            .Where(q => q.TypedOutcome!.ComponentCoverage is { Count: 2 })
            .ToArray();
        Assert.NotEmpty(twoComponent);

        foreach (var question in twoComponent)
        {
            var detail = question.TypedOutcome!;
            Assert.Equal(0.5, detail.RealisedGoldCoverage);
            Assert.Equal(TypedMemEvalEvidenceAttribution.EvidenceAbsent, detail.Attribution);

            var components = detail.ComponentCoverage!;
            Assert.Contains(components, c => c.Present);
            Assert.Contains(components, c => !c.Present);
            Assert.Contains(components, c => c.Kind == "statement");
            // Two shapes carry components since v4. The invalidated arm pairs a statement with
            // an invalidation; the still-valid control pairs it with a re-affirmation of the
            // same value, added so both arms carry the same G -- without it the control was the
            // hardest retrieval band in the family, on the arm whose job is to be the easy one.
            Assert.Contains(components, c => c.Kind is "invalidation" or "reaffirmation");
        }
    }

    [Fact]
    public async Task Coverage_TreatsANeverKnownProbeAsHavingNothingToMiss()
    {
        // A never-known probe has no gold. Coverage is vacuously complete; the question it asks is
        // whether the system invents something, not whether it retrieved anything.
        var result = await RunWithEvidenceAsync(
            TypedMemEvalVertical.Forgetting,
            useAnswerContext: true,
            goldSessionsToSurface: 0);

        var neverKnown = result.QuestionResults
            .Where(q => q.QuestionId.EndsWith("_abs", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(15, neverKnown.Length);
        Assert.All(neverKnown, q => Assert.Equal(1.0, q.TypedOutcome!.RealisedGoldCoverage));

        // Everything with gold, whose gold was withheld, is the opposite case.
        var withGold = result.QuestionResults
            .Where(q => !q.QuestionId.EndsWith("_abs", StringComparison.Ordinal))
            .ToArray();
        Assert.All(withGold, q => Assert.Equal(0.0, q.TypedOutcome!.RealisedGoldCoverage));
    }

    [Fact]
    public async Task Attribution_StaysAtReferenceLevelWhenNoContentWasSurfaced()
    {
        // References without content prove the session was cited, not that the fact survived the
        // memory system's own summarization. The weaker claim is the one that gets reported.
        var result = await RunWithEvidenceAsync(
            TypedMemEvalVertical.Forgetting,
            useAnswerContext: true,
            goldSessionsToSurface: int.MaxValue);

        Assert.Equal(TypedMemEvalAttributionLevel.Reference, result.TypedOutcomes!.Attribution.Level);
    }

    /// <summary>Questions whose gold exists — everything except the never-known probes.</summary>
    private static IEnumerable<QuestionResult> WithGold(ExternalBenchmarkResult result)
        => result.QuestionResults.Where(q => !q.QuestionId.EndsWith("_abs", StringComparison.Ordinal));

    private static async Task<ExternalBenchmarkResult> RunWithEvidenceAsync(
        TypedMemEvalVertical vertical,
        bool useAnswerContext,
        int goldSessionsToSurface)
    {
        var extensions = TypedMemEvalExtensions.Parse(TypedMemEvalCorpus.ReadJson(vertical));
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var agent = new EvidenceAgent(extensions, useAnswerContext, goldSessionsToSurface);

        return await runner.RunAsync(
            agent,
            vertical,
            new TypedMemEvalOptions
            {
                // The whole corpus, in corpus order: the fake below keys evidence off its own
                // invocation count, and a sampled subset would hand each question another
                // question's session ids.
                EvidenceCaptureMode = EvidenceCaptureMode.References
            });
    }

    /// <summary>
    /// An agent that reports evidence the way a real adapter does — through the
    /// <c>agenteval.question_evidence.v1</c> additional-properties contract — surfacing a
    /// controllable number of each question's gold sessions.
    /// </summary>
    private sealed class EvidenceAgent(
        IReadOnlyDictionary<string, TypedMemEvalExtension> extensions,
        bool useAnswerContext,
        int goldSessionsToSurface)
        : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent, ISessionResettableAgent
    {
        private int _index;

        public string Name => "evidence";

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
            // The runner walks the corpus in order, so the nth invocation belongs to the nth
            // selected question. Matching on prompt text would be fragile under grounding modes
            // that rewrite it.
            var extension = extensions.Values.ElementAt(_index++);
            var surfaced = extension.GoldSessionIndices.Take(goldSessionsToSurface).ToArray();

            var references = surfaced
                .Select((sessionIndex, rank) => new EvidenceReference
                {
                    Id = $"ref-{rank}",
                    Rank = rank + 1,
                    SourceSessionId = extension.SessionIds[sessionIndex],
                    AnswerContextOrder = useAnswerContext ? rank + 1 : null
                })
                .ToArray();

            var envelope = new QuestionEvidenceEnvelope
            {
                SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
                Retrieved = references,
                AnswerContext = useAnswerContext ? references : []
            };

            return Task.FromResult(new AgentResponse
            {
                Text = "an answer",
                AdditionalProperties = new Dictionary<string, object?>
                {
                    [QuestionEvidenceEnvelope.AdditionalPropertiesKey] =
                        JsonSerializer.Serialize(envelope)
                }
            });
        }
    }
}
