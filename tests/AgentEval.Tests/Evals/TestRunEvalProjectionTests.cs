// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// AE-04, first half: <c>(TestCase, TestResult) → EvalInput</c>.
/// </summary>
/// <remarks>
/// Every EvalInput field is asserted, including each one deliberately NOT carried — a field silently
/// dropped is the same defect as a field silently zeroed, so the non-carries are pinned here as
/// decisions rather than left to be discovered as bugs.
/// </remarks>
public class TestRunEvalProjectionTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TestCase FullyPopulatedCase() => new()
    {
        Id = "case-7",
        Name = "Refund flow — happy path",
        Input = "I want a refund for order 12345.",
        ExpectedOutputContains = "refund",
        EvaluationCriteria = new[] { "is polite", "states the policy" },
        PassingScore = 80,
        ExpectedTools = new[] { "lookup_order", "issue_refund" },
        GroundTruth = "Order 12345 is refundable.",
        Metadata = new Dictionary<string, object> { ["tier"] = "gold" },
        Tags = new[] { "refunds", "smoke" },
    };

    private static TestResult FullyPopulatedResult() => new()
    {
        TestName = "Refund flow — happy path",
        Passed = true,
        Score = 91,
        Details = "all good",
        ActualOutput = "Your refund is on its way.",
        Suggestions = new[] { "mention the timeline" },
    };

    // ══════════════════════════════════════════════════════════════════════════
    // The total mapping: every field of EvalInput, carried or not
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EveryEvalInputField_IsEitherCarriedFromTheRun_OrExplicitlyAbsent()
    {
        var testCase = FullyPopulatedCase();
        var result = FullyPopulatedResult();
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "lookup_order", CallId = "c1", Order = 1, Result = "found" });
        result.ToolUsage = usage;

        var input = testCase.ToEvalInput(result);

        // ── carried ──
        Assert.Equal("I want a refund for order 12345.", input.Query);
        Assert.Equal("Your refund is on its way.", input.Response);
        Assert.Equal("Order 12345 is refundable.", input.GroundTruth);
        Assert.Equal("case-7", input.CaseId);
        Assert.Equal(new[] { "lookup_order" }, input.ToolCalls!.Select(t => t.Name));
        Assert.Equal(new[] { "lookup_order", "issue_refund" }, input.ExpectedActions!.Select(a => a.Description));
        Assert.Equal("gold", input.Metadata!["tier"]);

        // ── deliberately NOT carried: nothing on either type is a source for these ──
        Assert.Null(input.Context);
        Assert.Null(input.SystemMessage);
        Assert.Null(input.SubjectModel);

        // ── deliberately NOT carried even though a plausible source exists ──
        // ToolDefinitions: see ToolDefinitions_AreNotProjected_BecauseNamesOnlyFlattersASchemaEval.
        Assert.Null(input.ToolDefinitions);
    }

    [Fact]
    public void TheCaseVerdict_IsNeverCarriedIntoTheStimulus()
    {
        // TestResult's Passed / Score / Details / Suggestions are a VERDICT. An EvalInput is a
        // STIMULUS. Feeding a prior verdict to the eval about to produce one is self-examination.
        // EvalInput has no field able to receive them; this test states that the projection does not
        // smuggle them into Metadata either.
        var input = FullyPopulatedCase().ToEvalInput(FullyPopulatedResult());

        Assert.NotNull(input.Metadata);
        Assert.Equal(new[] { "tier" }, input.Metadata!.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain(input.Metadata, kv => kv.Value is 91 or true);
    }

    [Fact]
    public void ProjectionIsPure_TheSameRunProjectsEqualInputsEveryTime()
    {
        var testCase = FullyPopulatedCase();
        var result = FullyPopulatedResult();

        var a = testCase.ToEvalInput(result);
        var b = testCase.ToEvalInput(result);

        Assert.Equal(a.Query, b.Query);
        Assert.Equal(a.Response, b.Response);
        Assert.Equal(a.CaseId, b.CaseId);
        Assert.Equal(a.GroundTruth, b.GroundTruth);
    }

    [Fact]
    public void NullArguments_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => FullyPopulatedCase().ToEvalInput(null!));
        Assert.Throws<ArgumentNullException>(() => ((TestCase)null!).ToEvalInput(FullyPopulatedResult()));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Refutation 1: the goal's signature. TestResult alone cannot supply a query.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheQueryComesFromTheCase_NotFromTheResult_WhichHasNoQueryField()
    {
        // TestResult exposes no query-shaped member at all, so `TestResult → EvalInput` cannot be a
        // total projection: it could only fabricate the one field EvalInput requires.
        var queryish = typeof(TestResult).GetProperties()
            .Where(p => p.Name is "Query" or "Input" or "Prompt")
            .ToList();

        Assert.Empty(queryish);
        Assert.Equal(FullyPopulatedCase().Input, FullyPopulatedCase().ToEvalInput(FullyPopulatedResult()).Query);
    }

    [Fact]
    public void ACaseWithANullInput_IsRefused_AndTheMessageNamesTheCase()
    {
        // The fourth way the projection could fail to be total, and the only one that used to pass
        // silently: `required string` is not `non-null`, EvalInput.Query is non-nullable, and the
        // null was carried straight through until the first eval to read Query threw an
        // unattributable NullReferenceException. Defaulting to "" is refused for the reason this
        // whole signature exists: it makes a fabricated stimulus look like a recorded one.
        var testCase = new TestCase { Id = "case-7", Name = "Refund flow", Input = null! };

        var ex = Assert.Throws<ArgumentException>(() => testCase.ToEvalInput(new TestResult { TestName = "n" }));

        Assert.Contains("case-7", ex.Message, StringComparison.Ordinal);
        Assert.Equal("testCase", ex.ParamName);
    }

    [Fact]
    public void TheOtherDirection_AnExplicitlyEmptyInput_IsCarriedThrough()
    {
        // A run that genuinely had no prompt is representable — the caller writes the empty string
        // and owns it. Only the null, which nobody chose, is refused.
        var input = new TestCase { Name = "n", Input = "" }.ToEvalInput(new TestResult { TestName = "n" });

        Assert.Equal("", input.Query);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Refutation 2: CaseId does not fall back to a display name
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CaseId_IsNullWhenTheCaseDeclaredNoId_AndDoesNotFallBackToTheTestName()
    {
        var testCase = new TestCase { Name = "Refund flow", Input = "q" };
        var result = new TestResult { TestName = "Refund flow" };

        var input = testCase.ToEvalInput(result);

        // "Nobody declared case identity" is the honest state. A name-shaped fallback would make
        // CaseId non-null on every case and would join on a display string — ADR-030 §4.7 D11.
        Assert.Null(input.CaseId);
        Assert.NotEqual("Refund flow", input.CaseId);
    }

    [Fact]
    public void ACallerWhoWantsTheNameAsIdentity_CanSaySoExplicitly()
    {
        var input = new TestCase { Name = "Refund flow", Input = "q" }
            .ToEvalInput(new TestResult { TestName = "Refund flow" }) with
        { CaseId = "Refund flow" };

        Assert.Equal("Refund flow", input.CaseId);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Refutation 3: ToolDefinitions from AvailableTools would flatter a real eval
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The refutation MEASURED rather than asserted: running the real
    /// <c>ToolInputAccuracyEval</c> both ways, with a stub judge so nothing is purchased.
    /// </summary>
    [Fact]
    public async Task Measured_ProjectingNamesOnlyToolDefinitions_FlipsARealEvalFromSkippedToPassing()
    {
        var call = new ToolCall("delete_account", new Dictionary<string, object>(), "ok");
        var withoutDefinitions = new EvalInput("q", "r", ToolCalls: new[] { call });
        var withNamesOnlyDefinitions = withoutDefinitions with
        {
            ToolDefinitions = new[] { new ToolDefinition("delete_account", null, null) },
        };
        var sut = new ToolInputAccuracyEval(new StubJudge());

        var schemaWhenAbsent = SchemaLeafOf(await sut.EvaluateAsync(withoutDefinitions));
        var schemaWhenNamesOnly = SchemaLeafOf(await sut.EvaluateAsync(withNamesOnlyDefinitions));

        // As shipped: the check cannot run, so it is SKIPPED and stays out of the denominator.
        Assert.Equal("skipped", schemaWhenAbsent.Provenance.Type);
        Assert.False(schemaWhenAbsent.Score.Passed);

        // With names-only definitions: `if (schemaParams is null) { passedCalls++; }` fires and the
        // leaf reports a perfect pass on a check that validated nothing. That lift is what this
        // projection would have manufactured, and it is why ToolDefinitions is not carried.
        Assert.NotEqual("skipped", schemaWhenNamesOnly.Provenance.Type);
        Assert.True(schemaWhenNamesOnly.Score.Passed);
        Assert.Equal(1.0, schemaWhenNamesOnly.Score.Value, 10);
    }

    private static EvalResult SchemaLeafOf(EvalResult composite) =>
        composite.Details.SubResults!.Single(r => r.Metric.Key == "tool_input_accuracy_schema");

    private sealed class StubJudge : AgentEval.Core.IEvaluator
    {
        // Deterministic, offline, and deliberately not a grade — this test reads the SCHEMA leaf.
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 50, Summary = "stub" });
    }

    [Fact]
    public void ToolDefinitions_AreNotProjected_BecauseNamesOnlyFlattersASchemaEval()
    {
        // ToolUsageReport.AvailableTools is a set of NAMES: no description, no parameter schema.
        // ToolInputAccuracyEval's schema leaf reads `if (schemaParams is null) { passedCalls++; }` —
        // "no parameter schema → treat as passing" — so projecting names to
        // ToolDefinition(name, null, null) would flip that leaf from Skipped to passing every call.
        // A silent lift in the flattering direction, manufactured by this projection.
        var usage = new ToolUsageReport();
        usage.DeclareAvailableTools(new[] { "delete_account", "lookup_order" });
        var result = FullyPopulatedResult();
        result.ToolUsage = usage;

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Null(input.ToolDefinitions);
        // The availability fact is not lost — it stays readable on the report itself.
        Assert.True(result.ToolUsage.WasToolAvailable("delete_account"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AE-02: ExpectedTools becomes readable for the first time
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExpectedTools_BecomeOneExpectedActionPerTool_InDeclaredOrder()
    {
        // One action per tool, not one action listing them all: TaskNavigationEfficiencyEval reads
        // ExpectedActions as a SEQUENCE and compares it position-by-position against the tool-call
        // names. A single action carrying all four tools presents a 4-step expectation as a 1-step
        // one and scores a correct run at 0.
        var testCase = new TestCase
        {
            Name = "n",
            Input = "q",
            ExpectedTools = new[] { "a", "b", "c" },
        };

        var actions = testCase.ToEvalInput(new TestResult { TestName = "n" }).ExpectedActions;

        Assert.NotNull(actions);
        Assert.Equal(3, actions!.Count);
        Assert.Equal(new[] { "a", "b", "c" }, actions.Select(x => x.Description));
        Assert.Equal(new[] { "a" }, actions[0].RequiredTools);
    }

    [Fact]
    public void ExpectedActions_SeparateNotDeclaredFromDeclaredEmpty()
    {
        var notDeclared = new TestCase { Name = "n", Input = "q", ExpectedTools = null }
            .ToEvalInput(new TestResult { TestName = "n" });
        var declaredEmpty = new TestCase { Name = "n", Input = "q", ExpectedTools = Array.Empty<string>() }
            .ToEvalInput(new TestResult { TestName = "n" });

        Assert.Null(notDeclared.ExpectedActions);
        Assert.NotNull(declaredEmpty.ExpectedActions);
        Assert.Empty(declaredEmpty.ExpectedActions!);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tool calls: null vs empty, ordering, and the source preference
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NoRecorderAtAll_YieldsNullToolCalls_NotAnEmptyList()
    {
        // null = "nobody recorded tool calls" (a NeverCallTool question is UNDECIDABLE).
        // [] = "a recorder ran and saw none" (decidable). An absence is not a zero.
        var input = FullyPopulatedCase().ToEvalInput(new TestResult { TestName = "n" });

        Assert.Null(input.ToolCalls);
    }

    [Fact]
    public void ARecorderThatSawNothing_YieldsAnEmptyList_NotNull()
    {
        var result = new TestResult { TestName = "n", ToolUsage = new ToolUsageReport() };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.NotNull(input.ToolCalls);
        Assert.Empty(input.ToolCalls!);
    }

    [Fact]
    public void AnEmptyTimeline_AlsoCountsAsARecorderThatSawNothing()
    {
        var result = new TestResult { TestName = "n", Timeline = new ToolCallTimeline() };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.NotNull(input.ToolCalls);
        Assert.Empty(input.ToolCalls!);
    }

    // ── A report that dropped approval-gated calls is not a record ────────────
    // ToolUsageReport.DroppedApprovalRequestCount is the library's own statement that Calls is
    // INCOMPLETE — "any absence-based policy (NeverCallTool) evaluated on this report has a chance
    // floor of 1.0". Every case below is ablated against its complete-report twin.

    [Fact]
    public void ADroppingReportThatRecordedNothing_YieldsNull_NotAMeasuredZero()
    {
        // The flattering collapse this prevents: the extractor could not SEE the approval-gated
        // calls, and [] would report that as "the agent called nothing" — a clean safety result
        // manufactured out of a blind recorder.
        var blind = new ToolUsageReport { DroppedApprovalRequestCount = 3 };
        var result = new TestResult { TestName = "n", ToolUsage = blind };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Null(input.ToolCalls);
    }

    [Fact]
    public void TheOtherDirection_TheSameEmptyReportWithNoDrops_StillYieldsAMeasuredZero()
    {
        // Direction control for the test above: the ONLY difference is the drop count, so the null
        // cannot be coming from "the report was empty".
        var seeing = new ToolUsageReport { DroppedApprovalRequestCount = 0 };
        var result = new TestResult { TestName = "n", ToolUsage = seeing };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.NotNull(input.ToolCalls);
        Assert.Empty(input.ToolCalls!);
    }

    [Fact]
    public void ADroppingReportThatRecordedSomeCalls_IsStillNotReturnedAsTheRecord()
    {
        // A partial list presented as a whole one makes an unseeable call look like one that never
        // happened. EvalInput has no channel for "this list is partial", so the honest answer is the
        // undecidable one — see the projection's remarks for what that costs and why.
        var partial = new ToolUsageReport { DroppedApprovalRequestCount = 1 };
        partial.AddCall(new ToolCallRecord { Name = "lookup_order", CallId = "c1", Order = 1, Result = "found" });
        var result = new TestResult { TestName = "n", ToolUsage = partial };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Null(input.ToolCalls);
    }

    [Fact]
    public void TheOtherDirection_TheSameCallsWithNoDrops_AreReturned()
    {
        var complete = new ToolUsageReport { DroppedApprovalRequestCount = 0 };
        complete.AddCall(new ToolCallRecord { Name = "lookup_order", CallId = "c1", Order = 1, Result = "found" });
        var result = new TestResult { TestName = "n", ToolUsage = complete };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Equal(new[] { "lookup_order" }, input.ToolCalls!.Select(c => c.Name));
    }

    [Fact]
    public void ADroppingReportIsBlind_EvenWhenATimelineIsAttached_SoToolCallsIsNull()
    {
        // SUPERSEDED: this test used to assert the timeline was read as a DIFFERENT recorder. No
        // producer satisfies that premise — `.Timeline = ` has four sites, all in
        // MAFEvaluationHarness.cs (:217, :246, :394, :453), and :129 fills it from this same report
        // via PopulateTimelineFromToolUsage (:599). Falling through therefore returned `[]` — a
        // MEASURED zero — for a run whose only calls were approval-gated, on exactly the
        // absence-based safety questions that cannot survive one.
        var blind = new ToolUsageReport { DroppedApprovalRequestCount = 2 };
        blind.AddCall(new ToolCallRecord { Name = "from_report", CallId = "c1", Order = 1 });
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "from_timeline", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });
        var result = new TestResult { TestName = "n", ToolUsage = blind, Timeline = timeline };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Null(input.ToolCalls);
    }

    [Fact]
    public void ACompleteReportBesideATimeline_ProjectsFromTheReport()
    {
        // The positive control for the test above: the guard must refuse a BLIND report, not any
        // report that happens to sit beside a timeline.
        var complete = new ToolUsageReport { DroppedApprovalRequestCount = 0 };
        complete.AddCall(new ToolCallRecord { Name = "from_report", CallId = "c1", Order = 1 });
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "from_timeline", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });
        var result = new TestResult { TestName = "n", ToolUsage = complete, Timeline = timeline };

        var input = FullyPopulatedCase().ToEvalInput(result);

        Assert.Equal(new[] { "from_report" }, input.ToolCalls!.Select(c => c.Name));
    }

    [Fact]
    public void ToolCalls_AreOrderedChronologically_EvenWhenRecordedOutOfOrder()
    {
        // EvalInput.ToolCalls' ordering is a CONTRACT (BUG-37): the approval gate treats an approval
        // as valid only if it appears at an earlier index than the sensitive call.
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "third", CallId = "c3", Order = 3 });
        usage.AddCall(new ToolCallRecord { Name = "first", CallId = "c1", Order = 1 });
        usage.AddCall(new ToolCallRecord { Name = "second", CallId = "c2", Order = 2 });
        var result = new TestResult { TestName = "n", ToolUsage = usage };

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(new[] { "first", "second", "third" }, calls.Select(c => c.Name));
    }

    [Fact]
    public void WhenNoProducerSetOrder_InsertionOrderIsPreserved()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "alpha", CallId = "c1" });
        usage.AddCall(new ToolCallRecord { Name = "beta", CallId = "c2" });
        var result = new TestResult { TestName = "n", ToolUsage = usage };

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(new[] { "alpha", "beta" }, calls.Select(c => c.Name));
    }

    [Fact]
    public void TimelineInvocations_AreOrderedBySequenceIndex()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "a", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "b", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(new[] { "a", "b" }, calls.Select(c => c.Name));
    }

    [Fact]
    public void TheTimelineStampsItsOwnSequenceIndex_WhichIsWhyTheProjectionsSortIsANoOpThere()
    {
        // ⚠ Honest scope of the timeline-path sort. ToolCallTimeline.AddInvocation OVERWRITES
        // SequenceIndex with the insertion position, so no producer using the public API can record
        // an out-of-order timeline and the projection's OrderBy can never be observed reordering
        // anything on this path. It is kept because EvalInput.ToolCalls' chronological ordering is a
        // contract (BUG-37) and this test is what would go red if that invariant were ever relaxed —
        // at which point the sort stops being decoration and starts being the thing that holds the
        // contract up. The ToolUsageReport path, where AddCall does NOT touch Order, is where the
        // sort is observable, and ToolCalls_AreOrderedChronologically_EvenWhenRecordedOutOfOrder
        // exercises it in both directions.
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "b", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true, SequenceIndex = 99 });
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "a", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true, SequenceIndex = 0 });

        Assert.Equal(new[] { 0, 1 }, timeline.Invocations.Select(i => i.SequenceIndex));
        Assert.Equal(new[] { "b", "a" }, timeline.Invocations.Select(i => i.ToolName));
    }

    [Fact]
    public void ToolUsage_WinsOverTimeline_BecauseItCarriesStructuredArgumentsAndRawResults()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "from_usage", CallId = "c1", Order = 1 });
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        { ToolName = "from_timeline", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });
        var result = new TestResult { TestName = "n", ToolUsage = usage, Timeline = timeline };

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(new[] { "from_usage" }, calls.Select(c => c.Name));
    }

    [Fact]
    public void AnEmptyToolUsageReport_FallsBackToAPopulatedTimeline()
    {
        var result = new TestResult { TestName = "n", ToolUsage = new ToolUsageReport(), Timeline = new ToolCallTimeline() };
        result.Timeline!.AddInvocation(new ToolInvocation
        { ToolName = "from_timeline", StartTime = TimeSpan.Zero, Duration = TimeSpan.Zero, Succeeded = true });

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(new[] { "from_timeline" }, calls.Select(c => c.Name));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 🔴 Hazard: ToolCallRecord.Result is object?, EvalInput.ToolCall.Result is string?
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AJsonElementResult_IsConverted_NotSilentlyNulled()
    {
        // The recorded failure this exists to prevent: AIFunctionFactory marshals a tool return
        // through JsonSerializer, so Result is typically a JsonElement. A naive `is string` test is
        // ALWAYS false on it, which produced a null on every call and floored a metric at zero.
        var element = JsonSerializer.Deserialize<JsonElement>("""{"orderId":12345,"refundable":true}""");
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord { Name = "lookup", CallId = "c1", Order = 1, Result = element });
        var result = new TestResult { TestName = "n", ToolUsage = usage };

        string? projected = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Result;

        Assert.NotNull(projected);
        Assert.Contains("12345", projected);
        Assert.Contains("refundable", projected);
    }

    [Theory]
    [InlineData("\"just a string\"", "just a string")]   // JSON string → its VALUE, unquoted
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]                          // a RECORDED json null: a value, not an absence
    [InlineData("[1,2]", "[1,2]")]
    public void EveryJsonValueKind_ConvertsToText(string json, string expected)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Equal(expected, TestRunEvalProjection.ResultOf(element, null));
    }

    [Fact]
    public void AnUndefinedJsonElement_IsReportedAsUnconvertible_NotAsNullAndNotAsAThrow()
    {
        // default(JsonElement).GetRawText() THROWS. A present-but-unrenderable value must not become
        // null: an absence and a value we failed to convert must not look the same.
        string? projected = TestRunEvalProjection.ResultOf(default(JsonElement), null);

        Assert.NotNull(projected);
        Assert.StartsWith(TestRunEvalProjection.UnconvertibleResultPrefix, projected);
    }

    [Fact]
    public void APlainStringResult_IsNotReSerialisedIntoQuotes()
    {
        Assert.Equal("found", TestRunEvalProjection.ResultOf("found", null));
    }

    [Fact]
    public void APocoResult_IsSerialised_NotDropped()
    {
        string? projected = TestRunEvalProjection.ResultOf(new { orderId = 7 }, null);

        Assert.NotNull(projected);
        Assert.Contains("7", projected);
    }

    [Fact]
    public void AResultThatCannotBeSerialised_StillProducesText()
    {
        // A self-referencing object defeats JsonSerializer; the fallback must still say something.
        var cyclic = new Cyclic();
        cyclic.Self = cyclic;

        string? projected = TestRunEvalProjection.ResultOf(cyclic, null);

        Assert.NotNull(projected);
        Assert.NotEqual(string.Empty, projected);
    }

    [Fact]
    public void NoResultAndNoFailure_IsNull_BecauseAVoidToolReallyReturnsNothing()
    {
        Assert.Null(TestRunEvalProjection.ResultOf(null, null));
    }

    [Fact]
    public void AFailedCallWithNoResult_IsMarkedAsAFailure_NotAsNothing()
    {
        // Otherwise a call that threw is indistinguishable from a void-returning call that worked.
        string? projected = TestRunEvalProjection.ResultOf(null, new InvalidOperationException("boom"));

        Assert.NotNull(projected);
        Assert.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, projected);
        Assert.Contains("boom", projected);
    }

    // ── A call that never ran must not look like one that ran and returned nothing ──

    [Fact]
    public void ARejectedCall_IsNotByteIdenticalToAVoidReturningSuccess()
    {
        // The defect, measured: both projected to ToolCall("delete_account", {}, null) and the two
        // records compared EQUAL. ToolCallRecord.WasExecuted exists to keep them apart — "a void- or
        // null-returning tool still executes, so absence of a result value does NOT mean
        // non-execution" — and the projection read neither it nor ApprovalState.
        var rejected = new ToolUsageReport();
        rejected.AddCall(new ToolCallRecord
        {
            Name = "delete_account",
            CallId = "c1",
            Order = 1,
            ApprovalState = ToolCallRecord.ApprovalRejected,
            WasExecuted = false,
        });
        var executed = new ToolUsageReport();
        executed.AddCall(new ToolCallRecord { Name = "delete_account", CallId = "c1", Order = 1, WasExecuted = true });

        var a = FullyPopulatedCase().ToEvalInput(new TestResult { TestName = "n", ToolUsage = rejected }).ToolCalls![0];
        var b = FullyPopulatedCase().ToEvalInput(new TestResult { TestName = "n", ToolUsage = executed }).ToolCalls![0];

        Assert.NotEqual(a, b);
        Assert.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, a.Result);
        Assert.Contains("rejected", a.Result!, StringComparison.Ordinal);
        Assert.Null(b.Result); // a void-returning tool really did return nothing
    }

    [Theory]
    [InlineData(ToolCallRecord.ApprovalRequested)]
    [InlineData(ToolCallRecord.ApprovalApproved)]
    public void AGatedCallWithNoPairedResult_IsNotKnownToHaveExecuted(string state)
    {
        // "Approved" is not "executed": the record's own docs say it executed only if a paired
        // result was also observed.
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        { Name = "issue_refund", CallId = "c1", Order = 1, ApprovalState = state, WasExecuted = false });

        string? projected = FullyPopulatedCase()
            .ToEvalInput(new TestResult { TestName = "n", ToolUsage = usage }).ToolCalls![0].Result;

        Assert.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, projected);
    }

    [Fact]
    public void TheOtherDirection_AnApprovedCallThatDidExecute_IsCarriedBare()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "issue_refund",
            CallId = "c1",
            Order = 1,
            ApprovalState = ToolCallRecord.ApprovalApproved,
            WasExecuted = true,
            Result = "refunded",
        });

        string? projected = FullyPopulatedCase()
            .ToEvalInput(new TestResult { TestName = "n", ToolUsage = usage }).ToolCalls![0].Result;

        Assert.Equal("refunded", projected);
    }

    [Fact]
    public void TheOtherDirection_WasExecutedFalseAloneChangesNothing_BecauseItMeansUnknown()
    {
        // ⚠ The guard is keyed on ApprovalState, never on WasExecuted alone. That flag defaults to
        // false and is set only by an extractor that matched a paired result, so on the ordinary
        // hand-built record false means UNKNOWN. Keying off it would have marked almost every
        // existing producer's calls as unexecuted.
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        { Name = "lookup_order", CallId = "c1", Order = 1, WasExecuted = false, Result = "found" });

        string? projected = FullyPopulatedCase()
            .ToEvalInput(new TestResult { TestName = "n", ToolUsage = usage }).ToolCalls![0].Result;

        Assert.Equal("found", projected);
    }

    [Fact]
    public void ARejectedCallThatAlsoCarriesAFailureResult_ReportsBoth()
    {
        // A rejection often synthesises a "failed" result. Non-execution leads, because it is the
        // stronger fact, and nothing recorded is discarded.
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "delete_account",
            CallId = "c1",
            Order = 1,
            ApprovalState = ToolCallRecord.ApprovalRejected,
            Exception = new InvalidOperationException("rejected by policy"),
        });

        string? projected = FullyPopulatedCase()
            .ToEvalInput(new TestResult { TestName = "n", ToolUsage = usage }).ToolCalls![0].Result;

        Assert.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, projected);
        Assert.Contains(TestRunEvalProjection.ToolErrorResultPrefix, projected!, StringComparison.Ordinal);
        Assert.Contains("rejected by policy", projected!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCallThatAlsoRecordedAPayload_StillReportsTheFailure()
    {
        // The defect: a tool that threw AFTER writing a partial result projected as that payload
        // alone. `Result is not null` outranked the exception, so `Succeeded`/`Exception` vanished
        // and the call was indistinguishable from one that worked — the flattering direction.
        string? projected = TestRunEvalProjection.ResultOf("partial payload", new InvalidOperationException("gateway timeout"));

        Assert.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, projected);
        Assert.Contains("gateway timeout", projected, StringComparison.Ordinal);
        Assert.Contains("partial payload", projected, StringComparison.Ordinal); // and nothing is lost
    }

    [Fact]
    public void TheOtherDirection_TheSamePayloadWithNoException_IsCarriedBare()
    {
        // Direction control: the ONLY difference from the test above is the exception, so the
        // prefix cannot be coming from "a payload was present".
        string? projected = TestRunEvalProjection.ResultOf("partial payload", null);

        Assert.Equal("partial payload", projected);
    }

    [Fact]
    public void AFailedTimelineInvocationThatAlsoRecordedAResult_StillReportsTheFailure()
    {
        // Same defect on the timeline path, where it is more reachable: ToolInvocation.Succeeded is
        // `required`, so EVERY producer declares it, and Result is commonly populated with the error
        // payload itself.
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "charge",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = false,
            ErrorMessage = "gateway timeout",
            Result = "partial payload",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        string? projected = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Result;

        Assert.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, projected);
        Assert.Contains("gateway timeout", projected, StringComparison.Ordinal);
        Assert.Contains("partial payload", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherDirection_ASucceedingTimelineInvocationCarriesItsResultBare()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "charge",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = true,
            Result = "partial payload",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        Assert.Equal("partial payload", FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Result);
    }

    [Fact]
    public void AFailedTimelineInvocation_CarriesItsErrorMessage()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "issue_refund",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = false,
            ErrorMessage = "gateway timeout",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        string? projected = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Result;

        Assert.NotNull(projected);
        Assert.StartsWith(TestRunEvalProjection.ToolErrorResultPrefix, projected);
        Assert.Contains("gateway timeout", projected);
    }

    private sealed class Cyclic
    {
        public Cyclic? Self { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tool-call arguments
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RecordedArguments_AreCarried_AndAbsentIsNotEmpty()
    {
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "with_args",
            CallId = "c1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["orderId"] = 12345 },
        });
        usage.AddCall(new ToolCallRecord { Name = "no_args_recorded", CallId = "c2", Order = 2 });
        usage.AddCall(new ToolCallRecord
        {
            Name = "empty_args",
            CallId = "c3",
            Order = 3,
            Arguments = new Dictionary<string, object?>(),
        });
        var result = new TestResult { TestName = "n", ToolUsage = usage };

        var calls = FullyPopulatedCase().ToEvalInput(result).ToolCalls!;

        Assert.Equal(12345, calls[0].Arguments!["orderId"]);
        Assert.Null(calls[1].Arguments);            // nothing captured
        Assert.Empty(calls[2].Arguments!);          // captured, and there were none
    }

    [Fact]
    public void AnArgumentRecordedAsNull_KeepsItsKey_AndNeverHandsAConsumerABareNull()
    {
        // "the argument was absent" and "the argument was passed as null" are different facts, and
        // IReadOnlyDictionary<string, object> promises a non-null value to whoever reads it.
        var usage = new ToolUsageReport();
        usage.AddCall(new ToolCallRecord
        {
            Name = "t",
            CallId = "c1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["note"] = null },
        });
        var result = new TestResult { TestName = "n", ToolUsage = usage };

        var arguments = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Arguments!;

        Assert.True(arguments.ContainsKey("note"));
        Assert.NotNull(arguments["note"]);
        Assert.Equal(JsonValueKind.Null, ((JsonElement)arguments["note"]).ValueKind);
    }

    [Fact]
    public void TimelineArguments_AreParsedFromJson()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "lookup",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = true,
            Arguments = """{"orderId":12345}""",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        var arguments = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Arguments!;

        Assert.True(arguments.ContainsKey("orderId"));
        Assert.Equal(12345, ((JsonElement)arguments["orderId"]).GetInt32());
    }

    [Fact]
    public void UnparseableTimelineArguments_ArePreserved_NotDropped()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "lookup",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = true,
            Arguments = "not json at all",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        var arguments = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Arguments!;

        Assert.Equal("not json at all", arguments[TestRunEvalProjection.RawArgumentsKey]);
    }

    [Fact]
    public void NonObjectTimelineArguments_ArePreserved_NotDropped()
    {
        var timeline = new ToolCallTimeline();
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "lookup",
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            Succeeded = true,
            Arguments = "[1,2,3]",
        });
        var result = new TestResult { TestName = "n", Timeline = timeline };

        var arguments = FullyPopulatedCase().ToEvalInput(result).ToolCalls![0].Arguments!;

        Assert.Equal("[1,2,3]", arguments[TestRunEvalProjection.RawArgumentsKey]);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Response and metadata: absence stays absence
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnUnrecordedResponse_StaysNull_AndAnEmptyOneStaysEmpty()
    {
        // 78.2% of one probe's answers were empty and counted as passes. "" and null are different
        // facts and both must survive the projection.
        var unrecorded = FullyPopulatedCase().ToEvalInput(new TestResult { TestName = "n" });
        var empty = FullyPopulatedCase().ToEvalInput(new TestResult { TestName = "n", ActualOutput = "" });

        Assert.Null(unrecorded.Response);
        Assert.Equal("", empty.Response);
    }

    [Fact]
    public void Metadata_IsCopied_SoLaterMutationOfTheCaseDoesNotRewriteTheStimulus()
    {
        var metadata = new Dictionary<string, object> { ["tier"] = "gold" };
        var testCase = new TestCase { Name = "n", Input = "q", Metadata = metadata };

        var input = testCase.ToEvalInput(new TestResult { TestName = "n" });
        metadata["tier"] = "bronze";

        Assert.Equal("gold", input.Metadata!["tier"]);
    }

    [Fact]
    public void Metadata_SeparatesNotSuppliedFromSuppliedEmpty()
    {
        var notSupplied = new TestCase { Name = "n", Input = "q" }
            .ToEvalInput(new TestResult { TestName = "n" });
        var suppliedEmpty = new TestCase { Name = "n", Input = "q", Metadata = new Dictionary<string, object>() }
            .ToEvalInput(new TestResult { TestName = "n" });

        Assert.Null(notSupplied.Metadata);
        Assert.NotNull(suppliedEmpty.Metadata);
        Assert.Empty(suppliedEmpty.Metadata!);
    }
}
