// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Runner behaviour: the typed report it produces, what it refuses to do, and the places where it
/// reports "not measured" instead of inventing a measurement.
/// </summary>
public sealed class TypedMemEvalRunnerTests
{
    [Fact]
    public async Task Run_ProducesATypedVectorThatSumsToItsOwnDenominator()
    {
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 6);
        var typed = result.TypedOutcomes!;
        var outcomes = typed.Outcomes;

        Assert.Equal(6, outcomes.N);
        Assert.Equal(
            outcomes.N,
            outcomes.Correct + outcomes.Wrong + outcomes.Abstained + outcomes.Missed +
            outcomes.Premature + outcomes.Inconclusive + outcomes.Unrun);

        // Every shape stratum publishes its own n, because most of them are five to twenty
        // questions and a rate without a denominator is how a small stratum gets read as a finding.
        Assert.NotEmpty(typed.ByShape);
        Assert.Equal(outcomes.N, typed.ByShape.Values.Sum(counts => counts.N));
    }

    [Fact]
    public async Task Run_KeepsTheBinaryFieldsPopulatedForCompatibility()
    {
        // The registered exception to "never one percentage" (ADR §9.5): the number exists so
        // generic tooling does not break, and the citation rule says it is not a TypedMemEval score.
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4);

        Assert.NotNull(result.OverallAccuracy);
        Assert.Equal(4, result.CorrectQuestions);
        Assert.NotNull(result.TypedOutcomes);
        Assert.Equal(4, result.TypedOutcomes!.Outcomes.Correct);
    }

    [Fact]
    public async Task Run_ReportsCoverageAsUnobservedWhenTheAgentSuppliesNoTelemetry()
    {
        // A run that cannot see coverage must say so. Printing zeros would read as "nothing was
        // retrieved", which is a different and much worse claim than "we could not see".
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.WorkingMemory, 5);
        var typed = result.TypedOutcomes!;

        Assert.Equal(0, typed.Coverage.Observed);
        Assert.Null(typed.Coverage.Mean);
        Assert.Equal(5, typed.Attribution.Unobserved);
        Assert.Equal(0, typed.Attribution.ObservedShare);
        Assert.All(
            result.QuestionResults,
            q =>
            {
                Assert.Null(q.TypedOutcome!.RealisedGoldCoverage);
                Assert.Equal(TypedMemEvalEvidenceAttribution.Unobserved, q.TypedOutcome.Attribution);
            });

        // The corpus's own calibrated floor still travels, so a later run with telemetry can be
        // read against the number the corpus was tuned to.
        Assert.NotNull(typed.Coverage.CalibratedFloorMean);
    }

    [Fact]
    public async Task WorkingMemory_ReportsACurveRatherThanAnAggregate()
    {
        // Averaging over a distance ladder destroys the only thing the vertical measures.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.WorkingMemory);

        var byDistance = result.TypedOutcomes!.ByDistance;
        Assert.NotNull(byDistance);
        // Five rungs since v4; the old bottom two could not fail at K_ref = 5.
        Assert.Equal([8, 15, 25, 40, 60], byDistance!.Keys.Order().ToArray());
        Assert.All(byDistance.Values, counts => Assert.Equal(12, counts.N));
    }

    [Fact]
    public async Task Prospective_ReportsPairConsistency()
    {
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Prospective);

        var pairs = result.TypedOutcomes!.PairConsistency;
        Assert.NotNull(pairs);
        Assert.Equal(19, pairs!.Pairs);
        Assert.Equal(19, pairs.BothArmsCorrect);

        // A fake judge that calls everything correct is NOT a time-blind system: gold flips between
        // the arms, so a genuinely time-blind answer produces Correct-then-Missed or
        // Premature-then-Correct, never Correct-then-Correct. This assertion pins that distinction,
        // because the first version of the metric counted identical outcomes and would have scored
        // this run 19/19 — reporting a time-blindness finding about a run that has none.
        Assert.Equal(0, pairs.TimeBlindPattern);
    }

    [Fact]
    public async Task Prospective_FlagsATimeBlindSystemByItsPairPattern()
    {
        // The finding the pair design exists to produce. This judge answers every question as
        // though the reminder had not yet fired, which is what a system reading a wall clock (or
        // receiving no query time at all) does — and because gold flips between the arms, that one
        // unchanging answer scores Correct on the before-arm and Missed on the after-arm.
        var runner = new TypedMemEvalRunner(new ArmAwareJudge());
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Prospective);

        var pairs = result.TypedOutcomes!.PairConsistency!;
        Assert.Equal(19, pairs.Pairs);
        Assert.Equal(0, pairs.BothArmsCorrect);
        Assert.Equal(19, pairs.MissedAfter);

        // TEN OF NINETEEN, AND THE SHORTFALL IS THE TEST DOUBLE, NOT THE DETECTOR. ArmAwareJudge
        // recognises a before-arm by three markers the generator guarantees on named-entity gold -
        // "Not yet.", "Yes, still valid", "It is still ahead". A due-window answer is a SET of
        // things falling due ("2: reorder the printer toner on 13 April 2026, ...") and carries
        // none of them, so the stub reads both window arms as after-arms and answers "missed" to
        // each. That is why MissedAfter is 19 while the correct-then-missed pattern is 10.
        //
        // What this test therefore covers is the ten named-entity pairs. Whether the outcome-pair
        // detector catches a genuinely time-blind system on the window shape is NOT established
        // here and should not be inferred from this number - a real such system returns the same
        // SET at both instants, whose label pattern depends on how the judge grades a partly-right
        // set. Establishing it needs a stub that can answer a window question.
        Assert.Equal(10, pairs.TimeBlindPattern);
    }

    [Fact]
    public async Task Forgetting_SeparatesStaleRecallFromOrdinaryWrongAnswers()
    {
        // Stale recall is a fabrication-shaped error, not an ordinary miss, and the whole vertical
        // exists to make that distinction visible.
        var runner = new TypedMemEvalRunner(
            new TypedMemEvalGuardTests.VerdictChatClient(
                "wrong", staleValueAsserted: true, claimedNoLongerKnown: true));
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Forgetting);

        var typed = result.TypedOutcomes!;
        Assert.Equal(20, typed.StaleRecall);          // only the invalidated shape can be stale
        Assert.Equal(15, typed.OverForgetting);       // every still-valid control graded wrong
        Assert.Equal(15, typed.MisattributedForgetting);
        Assert.Equal(50, typed.Outcomes.Wrong);
    }

    [Fact]
    public async Task Arithmetic_ReportsDurationQuestionsAsUnrunWhenTimestampsAreWithheld()
    {
        // A duration answer is recoverable only from session timestamps. Counting these as failures
        // would report a harness artefact as a memory result.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Arithmetic,
            new TypedMemEvalOptions { IncludeTimestamps = false });

        var typed = result.TypedOutcomes!;
        Assert.Equal(12, typed.Outcomes.Unrun);
        Assert.Equal(12, typed.UnrunReasons.Count);
        Assert.All(typed.UnrunReasons.Values, reason =>
            Assert.Contains("timestamps", reason, StringComparison.OrdinalIgnoreCase));

        // Unrun questions are excluded from the correct/wrong tally rather than counted against
        // the system, and the duration stratum shows them explicitly.
        Assert.Equal(12, typed.ByShape["duration"].Unrun);
        Assert.Equal(0, typed.ByShape["duration"].Wrong);
    }

    [Fact]
    public async Task Run_ResetsTheAgentOncePerQuestion()
    {
        // Pair arms and distance rungs are independent runs. A before-arm query that left retrieval
        // traces behind would contaminate its after-arm, and a WorkingMemory probe that rehearsed
        // the fact would measure refreshed memory rather than aged memory.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var agent = new TypedMemEvalGuardTests.RecordingAgent();

        await runner.RunAsync(
            agent, TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 5, RandomSeed = 1 });

        Assert.Equal(5, agent.Resets);
        Assert.Equal(5, agent.Prompts.Count);
    }

    [Fact]
    public async Task Prospective_DoesNotPrintTheQueryDateIntoThePrompt()
    {
        // Under TimestampsOnly the query time arrives through the typed channel. Printing it in the
        // prompt would hand back the crutch the whole vertical exists to remove.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var agent = new TypedMemEvalGuardTests.RecordingAgent();

        await runner.RunAsync(
            agent, TypedMemEvalVertical.Prospective,
            new TypedMemEvalOptions { MaxQuestions = 6, RandomSeed = 3 });

        Assert.All(agent.Prompts, prompt =>
            Assert.DoesNotContain("Current Date:", prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RecordsWhenTheAgentCannotCarryTheRequestedSampling()
    {
        // "Sent" and "honoured" are different things, and a benchmark that quietly did nothing here
        // would be worse than one that never offered the option.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));

        var result = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(),
            TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 3, RandomSeed = 5, AnswerSeed = 11 });

        Assert.NotNull(result.AnswerSampling);
        Assert.Equal(11, result.AnswerSampling!.RequestedSeed);
        Assert.False(result.AnswerSampling.Seed.CarriedByEveryQuestion);
        Assert.All(
            result.QuestionResults,
            q => Assert.Equal(
                AnswerSamplingDisposition.NotSupportedByAgent, q.AnswerSampling!.SeedDisposition));
    }

    [Fact]
    public async Task OracleArm_ProjectsThroughTheSharedProjectorAndReportsWhatItRealised()
    {
        // The consumer's ask across two prompts was that their ceiling and ours be the same number
        // from the same knobs, which is why the shipped oracle options keep their name here.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));

        var result = await runner.RunOracleAsync(
            new OracleAnswerClient(),
            TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 5, RandomSeed = 2 },
            LongMemEvalOracleOptions.GoldOnly);

        Assert.Contains("oracle", result.BenchmarkName, StringComparison.Ordinal);
        Assert.NotNull(result.OracleProjection);
        Assert.Equal(5, result.OracleProjection!.Questions);
        Assert.NotNull(result.TypedOutcomes);

        // Oracle runs are family results too, so the identity rule applies to them unchanged.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("longmemeval", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Family_RegistersOnePresetPerVerticalForDiscovery()
    {
        // A family that cannot be found by `bench --list` is a half-integration. One preset per
        // vertical rather than per size, because the verticals are not difficulty levels of one
        // thing — and there is deliberately no composite to make a "full" preset mean anything.
        _ = typeof(TypedMemEvalRunner).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("typedmemeval");
        Assert.NotNull(family);
        Assert.Equal(typeof(TypedMemEvalRunner), family!.RunnerType);
        // Derived, not hardcoded: the presets are built from TypedMemEvalVerticals.All, so a
        // literal here fails on the day a vertical is added and says nothing about what broke.
        Assert.Equal(TypedMemEvalVerticals.All.Count, family.Presets.Count);
        foreach (var descriptor in TypedMemEvalVerticals.All)
            Assert.Contains(family.Presets, p => p.Name == descriptor.Slug);

        // The description has to disclaim the comparison, because a one-line benchmark listing is
        // exactly where a reader would otherwise assume two memory benchmarks are commensurable.
        Assert.Contains("Not LongMemEval", family.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryRunner_IsBoundToItsPresetVertical()
    {
        using (new TypedMemEvalRunnerHostingContext(
                   new TypedMemEvalGuardTests.VerdictChatClient("correct")))
        {
            var family = BenchmarkFamilyRegistry.TryGet("typedmemeval")!;
            var runner = (TypedMemEvalRunner)family.RunnerFactory!("forgetting");

            Assert.Equal(TypedMemEvalVertical.Forgetting, runner.DefaultVertical);

            var result = await runner.RunAsync(
                new TypedMemEvalGuardTests.RecordingAgent(),
                new TypedMemEvalOptions { MaxQuestions = 3, RandomSeed = 1 });
            Assert.Equal("typedmemeval-forgetting", result.BenchmarkId);
        }
    }

    [Fact]
    public async Task DirectlyConstructedRunner_RefusesToGuessAVertical()
    {
        // Five verticals measuring five mechanisms have no sensible default between them.
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(new TypedMemEvalGuardTests.RecordingAgent()));
        Assert.Contains("no default to pick", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ForceTheFamilyInvariantsOntoThePipeline()
    {
        var descriptor = TypedMemEvalVerticals.For(TypedMemEvalVertical.Prospective);
        var mapped = new TypedMemEvalOptions
        {
            // Deliberately the mode the shipped validation rejects alongside temporal grounding.
            HistoryInjectionMode = HistoryInjectionMode.TextBlob
        }.ToExternalOptions(descriptor);

        mapped.Validate();

        Assert.Equal(HistoryInjectionMode.StructuredChatHistory, mapped.HistoryInjectionMode);
        Assert.Equal(TemporalGroundingMode.TimestampsOnly, mapped.TemporalGrounding);
        Assert.Equal(RunProvenanceMode.Full, mapped.RunProvenanceMode);
        Assert.Equal(JudgeVerdictProtocol.StructuredJson, mapped.JudgeVerdictProtocol);
        Assert.Equal(descriptor.CorpusId, mapped.DatasetMode);
        Assert.Null(mapped.DatasetPath);
    }

    [Fact]
    public void ControlArm_LabelsItselfWithoutChangingTheCorpus()
    {
        // Same corpus, same hash, two option sets. The pair only means something when both arms
        // run over identical questions.
        var descriptor = TypedMemEvalVerticals.For(TypedMemEvalVertical.Prospective);
        var probe = TypedMemEvalCorpus.ProspectiveProbeOptions.ToExternalOptions(descriptor);
        var control = TypedMemEvalCorpus.ProspectiveControlOptions.ToExternalOptions(descriptor);

        Assert.Equal(descriptor.CorpusId, probe.DatasetMode);
        Assert.Equal($"{descriptor.CorpusId}-control", control.DatasetMode);
        Assert.Equal(TemporalGroundingMode.TimestampsOnly, probe.TemporalGrounding);
        Assert.Equal(TemporalGroundingMode.TimestampsAndText, control.TemporalGrounding);
    }

    /// <summary>
    /// A judge standing in for a time-blind system: it grades every answer as though nothing had
    /// fired yet, which is Correct on a before-arm and Missed on an after-arm.
    /// </summary>
    private sealed class ArmAwareJudge : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // The gold text is in the prompt, and a before-arm's gold opens with an explicit not-yet.
            // Matched on the gold's own opening rather than a bare "still valid", which also appears
            // in the QUESTION of every expiring-validity pair and so matched both arms.
            var prompt = string.Join(" ", chatMessages.Select(m => m.Text));
            // The same three markers the generator guarantees on every before-arm gold
            // (gen_typedmemeval_prospective.check_pairs). "It is still ahead" arrived when the
            // not-yet-true shape was reframed to ask what the RECORD shows rather than whether
            // the thing had happened; keeping the sets aligned is what stops this fake from
            // silently mis-detecting an arm and reporting a time-blindness finding backwards.
            var beforeArm = prompt.Contains("Not yet.", StringComparison.Ordinal) ||
                            prompt.Contains("Yes, still valid", StringComparison.Ordinal) ||
                            prompt.Contains("It is still ahead", StringComparison.Ordinal);
            var outcome = beforeArm ? "correct" : "missed";
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $$"""{"outcome": "{{outcome}}", "reasoning": "time-blind stand-in"}""")));
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

    /// <summary>Answers oracle-projected questions with a fixed string.</summary>
    private sealed class OracleAnswerClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer")));

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
