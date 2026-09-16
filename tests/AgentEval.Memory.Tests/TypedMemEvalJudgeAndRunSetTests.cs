// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The judge's contract with the provider, and the aggregator's refusal to band runs that are not
/// comparable.
/// </summary>
public sealed class TypedMemEvalJudgeAndRunSetTests
{
    [Theory]
    [InlineData("correct", TypedMemEvalOutcome.Correct)]
    [InlineData("wrong", TypedMemEvalOutcome.Wrong)]
    [InlineData("abstained", TypedMemEvalOutcome.Abstained)]
    [InlineData("missed", TypedMemEvalOutcome.Missed)]
    [InlineData("premature", TypedMemEvalOutcome.Premature)]
    [InlineData("CORRECT", TypedMemEvalOutcome.Correct)]
    public void Verdict_ParsesEveryDeclaredOutcome(string raw, TypedMemEvalOutcome expected)
    {
        var parsed = TypedMemEvalVerdict.Parse($$"""{"outcome":"{{raw}}","reasoning":"because"}""");

        Assert.Equal(expected, parsed.Outcome);
        Assert.Equal("because", parsed.Reasoning);
        Assert.Null(parsed.FailureCode);
    }

    [Theory]
    [InlineData("", "empty_response")]
    [InlineData("no json here", "structured_no_json")]
    [InlineData("{not json}", "structured_malformed_json")]
    [InlineData("""{"reasoning":"x"}""", "structured_missing_outcome")]
    [InlineData("""{"outcome":42,"reasoning":"x"}""", "structured_outcome_not_string")]
    [InlineData("""{"outcome":"mostly right","reasoning":"x"}""", "structured_outcome_out_of_enum")]
    public void Verdict_RefusesToGuessFromAnythingUnusable(string raw, string expectedCode)
    {
        // A judge that guesses is a judge whose numbers cannot be defended. Every unusable response
        // gets a named failure code rather than a silent Wrong.
        var parsed = TypedMemEvalVerdict.Parse(raw);

        Assert.Null(parsed.Outcome);
        Assert.Equal(expectedCode, parsed.FailureCode);
    }

    [Fact]
    public void Verdict_RejectsTruncatedAndFilteredResponsesBeforeParsing()
    {
        // A truncated response cannot be trusted even when it happens to parse: the outcome field
        // may have been cut mid-token.
        Assert.Equal(
            "invalid_finish_reason",
            TypedMemEvalVerdict.Parse("""{"outcome":"correct","reasoning":"x"}""", "length").FailureCode);
        Assert.Equal(
            "content_filtered",
            TypedMemEvalVerdict.Parse("""{"outcome":"correct","reasoning":"x"}""", "content_filter").FailureCode);
    }

    [Fact]
    public void Verdict_RecoversJsonFromFencedOrPaddedResponses()
    {
        var fenced = TypedMemEvalVerdict.Parse(
            "Here is my verdict:\n```json\n{\"outcome\":\"missed\",\"reasoning\":\"denied it\"}\n```\nHope that helps.");

        Assert.Equal(TypedMemEvalOutcome.Missed, fenced.Outcome);
        Assert.Equal("denied it", fenced.Reasoning);
    }

    [Fact]
    public void Verdict_ReadsTheShapeSpecificFields()
    {
        var forgetting = TypedMemEvalVerdict.Parse(
            """{"outcome":"wrong","reasoning":"x","stale_value_asserted":true}""");
        Assert.True(forgetting.StaleValueAsserted);

        var listOrder = TypedMemEvalVerdict.Parse(
            """{"outcome":"wrong","reasoning":"x","ordered_pairs_correct":2,"ordered_pairs_total":3}""");
        Assert.Equal(2, listOrder.OrderedPairsCorrect);
        Assert.Equal(3, listOrder.OrderedPairsTotal);
    }

    [Fact]
    public void JudgeContract_IsChosenByShapeNotJustVertical()
    {
        // Asking for an ordering tally on a question with nothing to order invites the model to
        // invent one.
        var listOrder = Extension("episodic", "list-order");
        var assistantStated = Extension("episodic", "assistant-stated");
        var invalidated = Extension("forgetting", "invalidated");

        Assert.Equal(
            TypedMemEvalVerdict.Kind.ListOrder,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Episodic, listOrder));
        Assert.Equal(
            TypedMemEvalVerdict.Kind.Base,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Episodic, assistantStated));
        Assert.Equal(
            TypedMemEvalVerdict.Kind.Forgetting,
            TypedMemEvalJudge.SelectKind(TypedMemEvalVertical.Forgetting, invalidated));
    }

    [Fact]
    public void JudgeFingerprint_IsStableAndDisjointFromLongMemEvals()
    {
        // Two fingerprints that could collide would make judge drift in one benchmark invisible in
        // the other. The value is also stable across calls, or every run would look like a change.
        var first = TypedMemEvalJudge.PromptFingerprint;
        var second = TypedMemEvalJudgePrompts.Fingerprint;

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);

        var longMemEval = LongMemEvalProvenance.Capture(
            RunProvenanceMode.PromptsOnly, null, null, null)!.JudgePromptFingerprint;
        Assert.NotEqual(longMemEval, first);
    }

    [Fact]
    public void JudgePreamble_StatesEveryPrecedenceRuleTheReviewRatified()
    {
        // These are definitions, not style guidance. If one disappears from the template the
        // benchmark quietly starts measuring something else, so their presence is asserted.
        var preamble = TypedMemEvalJudgePrompts.Preamble;

        Assert.Contains("stated value outranks hedging", preamble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gold itself is a negative", preamble, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("premature", preamble, StringComparison.Ordinal);
        Assert.Contains("abstained", preamble, StringComparison.Ordinal);
        Assert.Contains("missed", preamble, StringComparison.Ordinal);

        var forgetting = TypedMemEvalJudgePrompts.Forgetting("q", "g", "a");
        Assert.Contains("it was a Honda", forgetting, StringComparison.Ordinal);
        Assert.Contains("stale_value_asserted", forgetting, StringComparison.Ordinal);
        Assert.Contains("never recorded", forgetting, StringComparison.Ordinal);

        var arithmetic = TypedMemEvalJudgePrompts.Arithmetic("q", "g", "a", "sum", "1, 2", "3", "USD");
        Assert.Contains("rounded value is correct if and only if", arithmetic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_BandsAcrossComparableRunsAndCountsFlips()
    {
        var first = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 5);
        var second = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 5);

        var summary = TypedMemEvalRunSet.Summarize([first, second]);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(5, summary.QuestionsCompared);
        Assert.Equal(0, summary.QuestionsWithFlips);
        Assert.Equal(5, summary.Outcomes[TypedMemEvalOutcome.Correct].Minimum);
        Assert.Equal(0, summary.Outcomes[TypedMemEvalOutcome.Correct].Width);

        // Two runs can agree by coincidence and band to zero width, which reads as perfect
        // stability and is not. The caller is told which it has.
        Assert.True(summary.AtMinimumRunCount);
    }

    [Fact]
    public async Task RunSet_SeesAFlipWhenTheOutcomeChanges()
    {
        var first = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4);
        var second = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4, "wrong");

        var summary = TypedMemEvalRunSet.Summarize([first, second]);

        Assert.Equal(4, summary.QuestionsWithFlips);
        Assert.Equal(4, summary.FlippedQuestionIds.Count);
        Assert.Equal(0, summary.Outcomes[TypedMemEvalOutcome.Correct].Minimum);
        Assert.Equal(4, summary.Outcomes[TypedMemEvalOutcome.Correct].Maximum);
    }

    [Fact]
    public async Task RunSet_RefusesRunsOfDifferentVerticals()
    {
        // Refusing is the feature. Banding non-comparable runs manufactures stability out of a
        // configuration change.
        var episodic = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);
        var forgetting = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Forgetting, 3);

        var error = Assert.Throws<TypedMemEvalRunSetMismatchException>(
            () => TypedMemEvalRunSet.Summarize([episodic, forgetting]));

        Assert.Equal("vertical", error.Dimension);
    }

    [Fact]
    public async Task RunSet_RefusesRunsWithDifferentConfigurations()
    {
        var runner = new TypedMemEvalRunner(new TypedMemEvalGuardTests.VerdictChatClient("correct"));
        var seeded = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 4, RandomSeed = 7 });
        var reseeded = await runner.RunAsync(
            new TypedMemEvalGuardTests.RecordingAgent(), TypedMemEvalVertical.Episodic,
            new TypedMemEvalOptions { MaxQuestions = 4, RandomSeed = 8 });

        var error = Assert.Throws<TypedMemEvalRunSetMismatchException>(
            () => TypedMemEvalRunSet.Summarize([seeded, reseeded]));

        Assert.Equal("options fingerprint", error.Dimension);
    }

    [Fact]
    public async Task RunSet_RefusesASingleRun()
    {
        var only = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);

        var error = Assert.Throws<ArgumentException>(() => TypedMemEvalRunSet.Summarize([only]));
        Assert.Contains("at least two runs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_RefusesResultsThatAreNotFamilyResults()
    {
        var typed = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);
        var foreign = new ExternalBenchmarkResult
        {
            BenchmarkId = "something-else",
            BenchmarkName = "Something Else",
            OverallAccuracy = 50,
            TaskAveragedAccuracy = 50,
            PerTypeResults = [],
            QuestionResults = [],
            Duration = TimeSpan.Zero,
            Options = new ExternalBenchmarkOptions()
        };

        var error = Assert.Throws<ArgumentException>(
            () => TypedMemEvalRunSet.Summarize([typed, foreign]));
        Assert.Contains("TypedOutcomes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSet_WarnsWhenAResultSetWouldDoubleCountTheSeededQuestions()
    {
        // The citation rule says the twelve carried questions must not be counted twice. A rule in
        // prose is one nobody's build enforces, so this is the runtime half.
        var prospective = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Prospective, 3);
        var episodic = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 3);

        Assert.Null(TypedMemEvalRunSet.DetectSeedOverlap([prospective, episodic]));

        var timeGrounded = new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "time-grounded probe",
            OverallAccuracy = null,
            TaskAveragedAccuracy = null,
            PerTypeResults = [],
            QuestionResults = [],
            Duration = TimeSpan.Zero,
            Provenance = new BenchmarkRunProvenance
            {
                Mode = RunProvenanceMode.Full,
                DatasetIdentifier = TypedMemEvalRunSet.TimeGroundedCorpusId
            },
            Options = new ExternalBenchmarkOptions()
        };

        var warning = TypedMemEvalRunSet.DetectSeedOverlap([prospective, timeGrounded]);
        Assert.NotNull(warning);
        Assert.Contains("double-counts", warning!, StringComparison.Ordinal);
        Assert.Contains("SeededFrom", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_CarriesTheNumbersAScoreMustBeReadAgainst()
    {
        // A report that shows only a score is worse than one that shows nothing: a full-haystack
        // 1.000 reads as a memory result unless the floor, the arms and the retriever travel with
        // it. Temporal is used because all three of its shapes publish a V9 arm.
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Temporal, 8);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        var shape = eval.Details.SubResults!
            .Single(n => n.Metric.Key == "typedmemeval.temporal.recency");

        // The numbers -- rendered by HtmlEvalResultRenderer as the node's "Metrics" table.
        Assert.Contains("floor.chance", shape.Details.Dimensions!.Keys);
        Assert.Contains("headroom.perfectSelector", shape.Details.Dimensions.Keys);
        Assert.Contains("arm.v8FullHaystack", shape.Details.Dimensions.Keys);
        Assert.Contains("arm.v9ReferenceRetrieval", shape.Details.Dimensions.Keys);

        // The V9 arm must not be silently equal to the full-haystack arm: that difference is the
        // whole reason the corpus exists, and an equal pair would mean the sidecar was misread.
        Assert.True(
            shape.Details.Dimensions["arm.v8FullHaystack"]
            > shape.Details.Dimensions["arm.v9ReferenceRetrieval"],
            "recency publishes a V8 above its V9; equal values mean the wrong fields were read.");

        // The words -- rendered as the node's bullet list.
        Assert.Contains(shape.Details.Recommendations!, r => r.StartsWith("Ranking:", StringComparison.Ordinal));

        // THE ROOT MUST NAME WHICH RETRIEVER CONDITIONS WHICH NUMBER, not just list them.
        // `headroom.perfectSelector` is V1 minus V9 and V9 is the BM25 reference arm — measured
        // 1.0000 - 0.5333 = 0.4667 on this shape. The dense pair decides the ranking class and
        // enters neither figure. An earlier version said all three conditioned "every retrieval
        // figure", which is the loose conditionality claim this family exists to stamp out.
        var referenceNote = Assert.Single(
            eval.Details.Recommendations!,
            r => r.StartsWith("Reference retriever:", StringComparison.Ordinal));
        Assert.Contains("bm25", referenceNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("headroom.perfectSelector", referenceNote, StringComparison.Ordinal);
        Assert.Contains("nothing else", referenceNote, StringComparison.Ordinal);

        var denseNote = Assert.Single(
            eval.Details.Recommendations!,
            r => r.StartsWith("Dense retrievers compared:", StringComparison.Ordinal));
        Assert.Contains("text-embedding-ada-002", denseNote, StringComparison.Ordinal);
        Assert.Contains("ranking class ONLY", denseNote, StringComparison.Ordinal);

        // The two must not be confused. Asserting the word "headroom" is ABSENT would be the
        // wrong check — the note mentions it precisely in order to DISCLAIM it. Assert the
        // disclaimer instead: it is the claim that matters, not the vocabulary.
        Assert.Contains("do not enter", denseNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_ProjectsAFamilySweepAsOneTree()
    {
        var temporal = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Temporal, 4);
        var episodic = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Episodic, 4);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(
            new[] { temporal, episodic }, judgeModel: "test-judge");

        Assert.Equal("typedmemeval", eval.Metric.Key);
        Assert.Equal(2, eval.Details.SubResults!.Count);
        Assert.Contains(eval.Details.SubResults, n => n.Metric.Key == "typedmemeval.temporal");
        Assert.Contains(eval.Details.SubResults, n => n.Metric.Key == "typedmemeval.episodic");

        // The denominator is summed over QUESTIONS, not averaged over verticals — a mean of two
        // rates would weight a 4-question vertical the same as a 40-question one.
        Assert.Equal(8.0, eval.Details.Dimensions!["n"]);
        Assert.Equal(2.0, eval.Details.Dimensions["verticals"]);

        // The root has to argue against its own number: ten verticals measure ten constructs, so
        // a mean over them answers no question. The score field is not nullable, so the only
        // available defence is saying so where the reader sees it.
        Assert.StartsWith("THIS NUMBER IS NOT A RESULT", eval.Details.Recommendations![0], StringComparison.Ordinal);
        Assert.Contains(eval.Details.Recommendations, r => r == TypedMemEvalEvalResultAdapter.CitationRule);

        // Depth is preserved all the way down: family -> vertical -> shape -> question.
        var vertical = eval.Details.SubResults.Single(n => n.Metric.Key == "typedmemeval.temporal");
        var shape = Assert.IsType<EvalResult>(vertical.Details.SubResults!.First());
        Assert.NotEmpty(shape.Details.SubResults!);
    }

    [Fact]
    public async Task Adapter_DoesNotInventAFamilyLevelForASingleVertical()
    {
        // NEGATIVE CONTROL. Wrapping one result in a "family" root would add a level that says
        // nothing and stack a second, identical score above the vertical's own.
        var one = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Temporal, 4);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(new[] { one }, judgeModel: "test-judge");

        Assert.Equal("typedmemeval.temporal", eval.Metric.Key);
        Assert.DoesNotContain(eval.Details.SubResults!, n => n.Metric.Key == "typedmemeval.temporal");
    }

    [Fact]
    public async Task Adapter_ProjectsEveryQuestionAsALeafUnderItsShape()
    {
        // The tree used to stop at shape level: root -> 3 shapes -> nothing, for a 50-question
        // run. A hierarchy whose leaves ARE the aggregate cannot be drilled into, and the
        // per-question results sat unused in the result object.
        const int asked = 8;
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Temporal, asked);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        var shapes = eval.Details.SubResults!;
        Assert.NotEmpty(shapes);

        // Every question asked appears exactly once, somewhere under its own shape.
        var leaves = shapes.SelectMany(sh => sh.Details.SubResults ?? []).ToList();
        Assert.Equal(asked, leaves.Count);

        // Ids are unique and match the ids the run actually recorded — not invented, not
        // duplicated across shapes.
        var leafIds = leaves.Select(l => l.Metric.Name).ToList();
        Assert.Equal(leafIds.Count, leafIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            result.QuestionResults.Select(q => q.QuestionId).OrderBy(x => x, StringComparer.Ordinal),
            leafIds.OrderBy(x => x, StringComparer.Ordinal));

        // Each leaf sits under the shape its own typed outcome names — a leaf filed under the
        // wrong parent would still produce the right COUNT above, so the parentage is asserted.
        foreach (var shapeNode in shapes)
        {
            var shapeName = shapeNode.Metric.Key.Split('.').Last();
            foreach (var leaf in shapeNode.Details.SubResults ?? [])
            {
                var recorded = result.QuestionResults.Single(q => q.QuestionId == leaf.Metric.Name);
                Assert.Equal(recorded.TypedOutcome!.Shape, shapeName);
                Assert.Contains(leaf.Details.Recommendations!, r => r.StartsWith("Outcome:", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task RenderedHtmlReport_ShowsTheContext_NotJustTheScore()
    {
        // WIRING, BOTH DIRECTIONS. The adapter carrying a dimension is worth nothing if the
        // renderer drops it. A real report written before this enrichment contained a "Metrics"
        // table and ZERO occurrences of the floor, the arms, the retriever id or the ranking
        // class -- it headlined "PASS 100.0%" for a run where the model was handed the whole
        // haystack. This asserts the rendered bytes, not the object graph.
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Temporal, 8);
        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        var bytes = await new HtmlEvalResultRenderer().RenderAsync(
            eval,
            new EvalResultRenderOptions(
                Subject: new SubjectIdentity(SubjectKind.Agent, "test", "test-model", "MAF"),
                Title: "TypedMemEval",
                RunId: "test-run"));

        var html = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("floor.chance", html, StringComparison.Ordinal);
        Assert.Contains("headroom.perfectSelector", html, StringComparison.Ordinal);
        Assert.Contains("arm.v9ReferenceRetrieval", html, StringComparison.Ordinal);
        Assert.Contains("Ranking:", html, StringComparison.Ordinal);
        Assert.Contains("text-embedding-ada-002", html, StringComparison.Ordinal);

        // And the aggregate must not stand alone: the reader has to be told not to cite it.
        Assert.Contains("Read the per-shape nodes", html, StringComparison.Ordinal);

        // THE LEAVES MUST SURVIVE RENDERING. Projecting per-question nodes is worth nothing if
        // the renderer stops recursing at depth 2 — the report would still show three rows and
        // the drill-down would exist only in the object graph.
        foreach (var id in result.QuestionResults.Select(q => q.QuestionId))
            Assert.Contains(id, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_AddsNothingWhenTheSidecarDoesNotCarryTheShape()
    {
        // NEGATIVE CONTROL. The enrichment must be additive and fail-soft: a projection is not
        // allowed to be the thing that fails a completed, paid-for run. `forgetting/never-known`
        // publishes an empty gold set and therefore no headroom fields at all, so it is the real
        // in-corpus case for "the sidecar has the shape but not the numbers".
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Forgetting, 12);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        var neverKnown = eval.Details.SubResults!
            .SingleOrDefault(n => n.Metric.Key == "typedmemeval.forgetting.never-known");
        if (neverKnown is null)
            return;   // the sampled subset did not reach that shape; nothing to assert

        // No headroom is published for it, so none may be invented.
        if (neverKnown.Details.Dimensions is { } dims)
        {
            Assert.DoesNotContain("headroom.perfectSelector", dims.Keys);
            Assert.DoesNotContain("arm.v9ReferenceRetrieval", dims.Keys);
        }

        // The typed vector still projects: enrichment adds, it never removes.
        Assert.NotNull(neverKnown.Score);
    }

    [Fact]
    public async Task Adapter_ProjectsTheTypedVectorAndCarriesTheCitationRule()
    {
        var result = await TypedMemEvalGuardTests.RunAsync(TypedMemEvalVertical.Forgetting, 6);

        var eval = TypedMemEvalEvalResultAdapter.ToEvalResult(result, judgeModel: "test-judge");

        Assert.Equal("typedmemeval.forgetting", eval.Metric.Key);
        Assert.Contains("outcome.correct", eval.Details.Dimensions!.Keys);
        Assert.Contains("outcome.missed", eval.Details.Dimensions.Keys);
        Assert.Contains("attribution.observedShare", eval.Details.Dimensions.Keys);
        Assert.Contains(
            TypedMemEvalEvalResultAdapter.CitationRule,
            eval.Details.Recommendations!);

        // The projection must not smuggle the other benchmark's name into a report — except inside
        // the citation rule itself, which has to name LongMemEval in order to disclaim it. Asserted
        // as "only there" rather than "nowhere", so the exception cannot widen unnoticed.
        var json = System.Text.Json.JsonSerializer.Serialize(eval);
        var withoutRule = json.Replace(
            System.Text.Json.JsonSerializer.Serialize(TypedMemEvalEvalResultAdapter.CitationRule).Trim('"'),
            "",
            StringComparison.Ordinal);
        Assert.DoesNotContain("longmemeval", withoutRule, StringComparison.OrdinalIgnoreCase);
    }

    private static TypedMemEvalExtension Extension(string vertical, string shape) => new()
    {
        Vertical = vertical,
        Shape = shape,
        GoldSessionIndices = [0],
        SessionIds = ["s000"]
    };
}
