// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// AE-01: fluent assertions now produce a result, without changing what xUnit sees.
/// <para>
/// The two consumers want opposite things and both are covered here: a test wants a failure to
/// THROW (unchanged), an eval run wants every outcome COLLECTED and nothing thrown. The third
/// axis these tests pin is that an assertion which <i>could not decide</i> — a missing-evidence
/// check, or one with a chance floor of 1.0 — is never reported as a pass.
/// </para>
/// </summary>
public class AssertionResultRecordingTests
{
    private static ToolUsageReport ReportWith(params string[] toolNames)
    {
        var report = new ToolUsageReport();
        for (var i = 0; i < toolNames.Length; i++)
        {
            report.AddCall(new ToolCallRecord
            {
                Name = toolNames[i],
                CallId = $"call-{i + 1}",
                Order = i + 1
            });
        }

        return report;
    }

    // ─── Direction 1: throwing still throws ──────────────────────────────────

    [Fact]
    public void NoScope_FailingAssertion_StillThrowsImmediately()
    {
        var report = ReportWith("Search");

        Assert.Throws<ToolAssertionException>(() => report.Should().HaveCalledTool("Missing"));
    }

    [Fact]
    public void ThrowScope_IsStillTheDefault_AndStillThrowsOnDispose()
    {
        var report = ReportWith("Search");

        var ex = Assert.Throws<AgentEvalScopeException>(() =>
        {
            using var scope = new AgentEvalScope();
            Assert.Equal(AgentEvalScopeMode.Throw, scope.Mode);
            report.Should().HaveCalledTool("Missing");
        });

        Assert.Single(ex.Failures);
    }

    [Fact]
    public void ThrowScope_RecordsResultsToo_SoAReportIsAvailableAlongsideTheException()
    {
        var report = ReportWith("Search");
        AgentEvalScope? captured = null;

        Assert.Throws<AgentEvalScopeException>(() =>
        {
            using var scope = new AgentEvalScope();
            captured = scope;
            report.Should().HaveCalledTool("Search");   // passes
            report.Should().HaveCalledTool("Missing");  // fails
        });

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Results.Count);
        Assert.Equal(1, captured.PassedCount);
        Assert.Equal(1, captured.FailureCount);
    }

    // ─── Direction 2: collecting collects, and never throws ──────────────────

    [Fact]
    public void CollectingScope_DoesNotThrowOnDispose_AndYieldsResults()
    {
        var report = ReportWith("Search");

        var scope = AgentEvalScope.Collecting("eval run");
        Assert.Equal(AgentEvalScopeMode.Collect, scope.Mode);
        report.Should().HaveCalledTool("Missing");

        var ex = Record.Exception(() => scope.Dispose());

        Assert.Null(ex); // the whole point: an eval run gets a report, not an exception
        Assert.True(scope.HasFailures);
        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Failed, result.Outcome);
        Assert.Equal("HaveCalledTool(Missing)", result.Assertion);
    }

    [Fact]
    public void CollectingScope_ResultsAreReadableAfterDispose()
    {
        var report = ReportWith("Search");

        using (var scope = AgentEvalScope.Collecting())
        {
            report.Should().HaveCalledTool("Search");
            scope.Dispose();

            Assert.Single(scope.Results);
            Assert.Equal(AssertionOutcome.Passed, scope.Results[0].Outcome);
        }
    }

    [Fact]
    public void CollectingScope_NestedContextScope_FlowsResultsUpTaggedWithItsContext()
    {
        var report = ReportWith("Search");

        var outer = AgentEvalScope.Collecting();
        using (var child = outer.WithContext("phase-1"))
        {
            Assert.Equal(AgentEvalScopeMode.Collect, child.Mode); // mode is inherited, not reset
            report.Should().HaveCalledTool("Search");
        }

        outer.Dispose();

        var result = Assert.Single(outer.Results);
        Assert.Equal("[phase-1] HaveCalledTool(Search)", result.Assertion);
    }

    // ─── A pass is recorded ──────────────────────────────────────────────────

    [Fact]
    public void PassingAssertion_IsRecorded_SoFourOfFiveIsKnowable()
    {
        var report = ReportWith("Alpha", "Beta", "Gamma");

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Alpha");
        report.Should().HaveCalledTool("Beta");
        report.Should().HaveCalledTool("Gamma");
        report.Should().HaveCallCount(3);
        report.Should().HaveCallCount(99); // the one failure
        scope.Dispose();

        Assert.Equal(5, scope.Results.Count);
        Assert.Equal(4, scope.PassedCount);
        Assert.Equal(1, scope.FailureCount);
        Assert.Equal(0, scope.InconclusiveCount);
    }

    [Fact]
    public void RecordPass_FromCustomAssertion_IsCollected()
    {
        using var scope = AgentEvalScope.Collecting();
        AgentEvalScope.RecordPass("MyCustomCheck", "held for the right reason");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal("MyCustomCheck", result.Assertion);
        Assert.Equal(AssertionOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void RecordPass_WithNoScope_IsANoOp()
    {
        var ex = Record.Exception(() => AgentEvalScope.RecordPass("NoScope"));
        Assert.Null(ex);
    }

    [Fact]
    public void NestedAssertions_RecordOnce_NotTwice()
    {
        // ContainAll(params) delegates to ContainAll(because, params). Passing an explicit string[]
        // pins the single-parameter overload, so this really is the nested shape; the inner probe
        // must not add a second row for the same user-visible call.
        using var scope = AgentEvalScope.Collecting();
        "hello world".Should().ContainAll(new[] { "hello", "world" });
        scope.Dispose();

        Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Passed, scope.Results[0].Outcome);
    }

    // ─── Every instrumented family records, one row per call ─────────────────
    //
    // These are the regression guard for the instrumentation sweep itself: an assertion whose
    // `return` was not routed through probe.Complete(...) records Inconclusive instead of the
    // pass it earned, and an over-eager probe records twice. Both show up as a wrong count here.

    [Fact]
    public void ResponseAssertions_EveryPassingCheck_RecordsExactlyOnePass()
    {
        const string response = "Hello brave new world";

        using var scope = AgentEvalScope.Collecting();
        response.Should().Contain("brave");
        response.Should().ContainAll(new[] { "Hello", "world" });
        response.Should().ContainAll(because: null, "Hello", "world");
        response.Should().ContainAny(new[] { "nope", "world" });
        response.Should().ContainAny(because: null, "nope", "world");
        response.Should().NotContain("zzz");
        response.Should().MatchPattern("br.ve");
        response.Should().HaveLengthBetween(1, 100);
        response.Should().HaveLengthAtLeast(3);
        response.Should().NotBeEmpty();
        response.Should().StartWith("Hello");
        response.Should().EndWith("world");
        scope.Dispose();

        Assert.Equal(12, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void ToolUsageAssertions_EveryPassingCheck_RecordsExactlyOnePass()
    {
        var report = ReportWith("Alpha", "Beta");
        report.DeclareAvailableTools(new[] { "Alpha", "Beta", "Forbidden" });

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Alpha");
        report.Should().NotHaveCalledTool("Gamma");
        report.Should().HaveCallCount(2);
        report.Should().HaveCallCountAtLeast(1);
        report.Should().HaveNoErrors();
        report.Should().HaveCallOrder("Alpha", "Beta");
        report.Should().HaveCalledAnyTool();
        report.Should().NeverCallTool("Forbidden", because: "declared but unused");
        report.Should().NeverPassArgumentMatching(@"\d{3}-\d{2}-\d{4}", because: "no SSNs");
        report.Should().HaveCalledTool("Alpha").BeforeTool("Beta");
        report.Should().HaveCalledTool("Beta").AfterTool("Alpha");
        report.Should().HaveCalledTool("Alpha").WithoutError();
        report.Should().HaveCalledTool("Alpha").Times(1);
        scope.Dispose();

        // 13 statements, but the four chained ones each record their HaveCalledTool row too.
        Assert.Equal(17, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void PerformanceAssertions_EveryPassingCheck_RecordsExactlyOnePass()
    {
        var start = DateTimeOffset.UnixEpoch;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddMilliseconds(500),
            TimeToFirstToken = TimeSpan.FromMilliseconds(50),
            PromptTokens = 100,
            CompletionTokens = 50,
            EstimatedCost = 0.001m,
            ToolCallCount = 2,
            TotalToolTime = TimeSpan.FromMilliseconds(100)
        };

        using var scope = AgentEvalScope.Collecting();
        metrics.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
        metrics.Should().HaveTotalDurationAtLeast(TimeSpan.Zero);
        metrics.Should().HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(5));
        metrics.Should().HaveTokenCountUnder(10_000);
        metrics.Should().HavePromptTokensUnder(10_000);
        metrics.Should().HaveCompletionTokensUnder(10_000);
        metrics.Should().HaveEstimatedCostUnder(1.0m);
        metrics.Should().HaveAverageToolTimeUnder(TimeSpan.FromSeconds(5));
        metrics.Should().HaveTotalToolTimeUnder(TimeSpan.FromSeconds(5));
        metrics.Should().HaveToolCallCount(2);
        scope.Dispose();

        Assert.Equal(10, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void SkillUsageAssertions_PassingChecks_RecordAPassPerCall()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = AgentEval.Skills.SkillToolNames.LoadSkill,
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["skill_name"] = "expense-report" }
        });

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveLoadedSkill("expense-report");
        report.Should().NotHaveRunSkillScript("no scripts should run");
        scope.Dispose();

        Assert.Equal(2, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void CopilotStudioAssertions_PassingCheck_RecordsAPass()
    {
        using var scope = AgentEvalScope.Collecting();
        "a real answer".Should().HaveRespondedWithNonEmptyMessage();
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Passed, result.Outcome);
        Assert.Equal("HaveRespondedWithNonEmptyMessage", result.Assertion);
    }

    [Fact]
    public void FailureRaisedOutsideAnyProbe_StillReachesTheReport()
    {
        using var scope = AgentEvalScope.Collecting();
        AgentEvalScope.FailWith("a bare failure with no assertion around it");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AgentEvalScope.UnattributedAssertionName, result.Assertion);
        Assert.Equal(AssertionOutcome.Failed, result.Outcome);
    }

    // ─── An assertion that could not decide is not a pass ────────────────────

    [Fact]
    public void NeverCallTool_WithNoDeclaredInventory_IsInconclusive_NotAPass()
    {
        // Chance floor 1.0: nothing in the report can distinguish "the agent had PlaceOrder and
        // refrained" from "PlaceOrder was never on offer". Reporting green here would be the
        // flattering direction.
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        report.Should().NeverCallTool("PlaceOrder", because: "orders need a human");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, result.Outcome);
        Assert.False(result.Passed);
        Assert.True(result.IsInconclusive);
        Assert.Equal(0, scope.PassedCount);
        Assert.Equal(0, scope.FailureCount); // undecidable is not a failure either
        Assert.Contains("inventory was not declared", result.Message);
    }

    [Fact]
    public void NeverCallTool_WhenToolWasNotEvenAvailable_SaysSo()
    {
        var report = ReportWith("Search");
        report.DeclareAvailableTools(new[] { "Search", "Lookup" });

        using var scope = AgentEvalScope.Collecting();
        report.Should().NeverCallTool("PlaceOrder", because: "orders need a human");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, result.Outcome);
        Assert.Contains("not among the agent's declared tools", result.Message);
    }

    [Fact]
    public void NeverCallTool_WhenToolWasAvailableAndNotCalled_IsARealPass()
    {
        // Declaring the inventory is what turns the check from vacuous into evidence.
        var report = ReportWith("Search");
        report.DeclareAvailableTools(new[] { "Search", "PlaceOrder" });

        using var scope = AgentEvalScope.Collecting();
        report.Should().NeverCallTool("PlaceOrder", because: "orders need a human");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void NeverCallTool_WhenViolated_StillFails_RegardlessOfInventory()
    {
        var report = ReportWith("PlaceOrder");

        using var scope = AgentEvalScope.Collecting();
        report.Should().NeverCallTool("PlaceOrder", because: "orders need a human");
        scope.Dispose();

        Assert.Equal(1, scope.FailureCount);
        Assert.Equal(AssertionOutcome.Failed, Assert.Single(scope.Results).Outcome);
    }

    [Fact]
    public void MustConfirmBefore_WhenTheGuardedToolWasNeverCalled_IsInconclusive()
    {
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        report.Should().MustConfirmBefore("TransferFunds", because: "money moves need approval");
        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, result.Outcome);
        Assert.Contains("never exercised", result.Message);
    }

    [Fact]
    public void WithDurationUnder_WithoutTiming_IsInconclusive_NotAPass()
    {
        var report = ReportWith("Search"); // no StartTime/EndTime → HasTiming is false

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Search").WithDurationUnder(TimeSpan.FromSeconds(1));
        scope.Dispose();

        Assert.Equal(2, scope.Results.Count);
        Assert.Equal(AssertionOutcome.Passed, scope.Results[0].Outcome);       // HaveCalledTool
        Assert.Equal(AssertionOutcome.Inconclusive, scope.Results[1].Outcome); // WithDurationUnder
        Assert.Equal(1, scope.InconclusiveCount);
    }

    [Fact]
    public void ChainedAssertionAfterAMissingTool_IsInconclusive_NotAPass()
    {
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Missing").WithoutError();
        scope.Dispose();

        Assert.Equal(2, scope.Results.Count);
        Assert.Equal(AssertionOutcome.Failed, scope.Results[0].Outcome);
        Assert.Equal(AssertionOutcome.Inconclusive, scope.Results[1].Outcome);
    }

    [Fact]
    public void PerformanceAssertion_WithoutTheEvidenceItNeeds_IsInconclusive()
    {
        var metrics = new PerformanceMetrics(); // no TTFT, no cost, no tool timing

        using var scope = AgentEvalScope.Collecting();
        metrics.Should().HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(1));
        metrics.Should().HaveEstimatedCostUnder(0.10m);
        metrics.Should().HaveTotalToolTimeUnder(TimeSpan.FromSeconds(1));
        scope.Dispose();

        Assert.Equal(3, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Inconclusive, r.Outcome));
        Assert.Equal(0, scope.PassedCount);
    }

    [Fact]
    public void ProbeDisposedWithoutCompleting_RecordsInconclusive_NotAPass()
    {
        // An exception escaping an assertion body must not leave a green row behind: the probe
        // only records a pass when the assertion said it ran to completion.
        using var scope = AgentEvalScope.Collecting();

        Action crashingAssertion = () =>
        {
            using var probe = AgentEvalScope.BeginAssertion("subject", "CrashingAssertion");
            throw new InvalidOperationException("boom");
        };

        Assert.Throws<InvalidOperationException>(crashingAssertion);

        scope.Dispose();

        var result = Assert.Single(scope.Results);
        Assert.Equal("CrashingAssertion(subject)", result.Assertion);
        Assert.Equal(AssertionOutcome.Inconclusive, result.Outcome);
        Assert.Contains("did not run to completion", result.Message);
    }

    [Fact]
    public void BeginAssertion_WithNoScope_IsInactiveAndAllocatesNothingObservable()
    {
        using var probe = AgentEvalScope.BeginAssertion("x", "SomeAssertion");
        Assert.False(probe.IsActive);
        probe.MarkInconclusive("ignored"); // must not throw
    }

    // ─── The canonical AssertionResult ───────────────────────────────────────

    [Fact]
    public void AssertionResult_LegacyThreeArgumentShape_StillWorks_AndDerivesItsOutcome()
    {
        var pass = new AssertionResult("check", true, null);
        var fail = new AssertionResult("check", false, "why");

        Assert.Equal(AssertionOutcome.Passed, pass.Outcome);
        Assert.Equal(AssertionOutcome.Failed, fail.Outcome);
        Assert.Equal("check", pass.Name); // Name alias for the collapsed Testing vocabulary
    }

    [Fact]
    public void AssertionResult_Undecidable_IsNotAPass()
    {
        var undecidable = AssertionResult.Undecidable("check", "no evidence");

        Assert.False(undecidable.Passed);
        Assert.True(undecidable.IsInconclusive);
        Assert.Equal(AssertionOutcome.Inconclusive, undecidable.Outcome);
    }

    [Fact]
    public void AssertionResult_CannotBeConstructedClaimingAPassItDidNotEarn()
    {
        // The invariant is structural, not a convention: a green Passed with a non-green Outcome
        // is rejected at construction.
        Assert.Throws<ArgumentException>(() =>
            new AssertionResult("check", true, null) { Outcome = AssertionOutcome.Inconclusive });

        Assert.Throws<ArgumentException>(() =>
            new AssertionResult("check", false, null) { Outcome = AssertionOutcome.Passed });
    }

    [Fact]
    public void AssertionResult_WithExpression_CannotPromoteAnUndecidableIntoAPass()
    {
        var undecidable = AssertionResult.Undecidable("check", "no evidence");

        var copied = undecidable with { Assertion = "renamed" };

        Assert.Equal(AssertionOutcome.Inconclusive, copied.Outcome);
    }

    [Fact]
    public void LegacyTestingAssertionResult_ConvertsBothWays()
    {
#pragma warning disable CS0618 // exercising the obsolete alias on purpose
        AgentEval.Testing.AssertionResult legacy = new("check", true, "message");
        AssertionResult canonical = legacy!;
        AgentEval.Testing.AssertionResult roundTripped = canonical!;

        Assert.Equal("check", canonical.Assertion);
        Assert.True(canonical.Passed);
        Assert.Equal(legacy, roundTripped);
#pragma warning restore CS0618
    }

    // ─── The result reaches the artifacts AgentEval writes ───────────────────

    [Fact]
    public void ToScenarioResult_PicksUpTheAmbientScopesResults()
    {
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Search");
        report.Should().HaveCalledTool("Missing");

        var evalResult = EvalResultFixture();
        var scenario = EvalResultPersistence.ToScenarioResult(evalResult, "scen-1", "Scenario 1");

        Assert.Equal(2, scenario.Assertions.Count);
        Assert.Equal(AssertionOutcome.Passed, scenario.Assertions[0].Outcome);
        Assert.Equal(AssertionOutcome.Failed, scenario.Assertions[1].Outcome);

        scope.Clear(); // avoid nothing — Collect mode never throws, but keep the scope tidy
    }

    [Fact]
    public void ToScenarioResult_ExplicitAssertionsWinOverTheAmbientScope()
    {
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Search");

        var scenario = EvalResultPersistence.ToScenarioResult(
            EvalResultFixture(), "scen-1", "Scenario 1",
            new[] { AssertionResult.Fail("explicit", "supplied by the caller") });

        var only = Assert.Single(scenario.Assertions);
        Assert.Equal("explicit", only.Assertion);
    }

    [Fact]
    public void ToScenarioResult_WithNoScopeAndNoArgument_IsEmpty_AsBefore()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(
            EvalResultFixture(), "scen-1", "Scenario 1");

        Assert.Empty(scenario.Assertions);
    }

    // ─── Discrimination, on disk ─────────────────────────────────────────────

    [Theory]
    [InlineData(2)] // 5 assertions, 2 failing
    [InlineData(0)] // 5 assertions, none failing — the case a failures-only fix gets wrong
    public async Task PersistedAssertions_KeepTheFullCount_NotOnlyTheFailures(int failing)
    {
        using var temp = Output.TempWorkspace.Create("AssertionRoundTrip");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "AssertionAgent");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        var report = ReportWith("Alpha", "Beta", "Gamma", "Delta", "Epsilon");

        using var scope = AgentEvalScope.Collecting();
        report.Should().HaveCalledTool("Alpha");
        report.Should().HaveCalledTool("Beta");
        report.Should().HaveCalledTool("Gamma");
        report.Should().HaveCalledTool(failing >= 1 ? "NotThere1" : "Delta");
        report.Should().HaveCalledTool(failing >= 2 ? "NotThere2" : "Epsilon");
        scope.Dispose();

        var scenario = EvalResultPersistence.ToScenarioResult(
            EvalResultFixture(), "scen-1", "Scenario 1", scope.Results);
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);

        // Read back off disk: the defect this covers is in persistence, so an in-memory
        // comparison would prove nothing.
        var readBack = new List<ScenarioResult>();
        await foreach (var sr in store.GetScenarioResultsAsync(manifest.Run.RunId))
            readBack.Add(sr);

        var persisted = Assert.Single(readBack);
        Assert.Equal(5, persisted.Assertions.Count);
        Assert.Equal(failing, persisted.Assertions.Count(a => a.Outcome == AssertionOutcome.Failed));
        Assert.Equal(5 - failing, persisted.Assertions.Count(a => a.Outcome == AssertionOutcome.Passed));
    }

    [Fact]
    public async Task PersistedInconclusive_SurvivesTheRoundTrip_AndDoesNotReadBackAsAPass()
    {
        using var temp = Output.TempWorkspace.Create("InconclusiveRoundTrip");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "AssertionAgent");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        var scenario = EvalResultPersistence.ToScenarioResult(
            EvalResultFixture(), "scen-1", "Scenario 1",
            new[] { AssertionResult.Undecidable("NeverCallTool(PlaceOrder)", "never available") });
        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);

        var readBack = new List<ScenarioResult>();
        await foreach (var sr in store.GetScenarioResultsAsync(manifest.Run.RunId))
            readBack.Add(sr);

        var row = Assert.Single(Assert.Single(readBack).Assertions);
        Assert.Equal(AssertionOutcome.Inconclusive, row.Outcome);
        Assert.False(row.Passed);
    }

    [Fact]
    public async Task CollectingScope_OpenedBeforeAnAwait_StillCollectsOnTheContinuationThread()
    {
        // MNT-07's AsyncLocal must survive the collect-mode addition: a scope opened before an
        // await has to see assertions that run after it, on whatever thread they land on.
        var report = ReportWith("Search");

        using var scope = AgentEvalScope.Collecting();
        await Task.Run(() =>
        {
            report.Should().HaveCalledTool("Search");
            report.Should().HaveCalledTool("Missing");
        });
        scope.Dispose();

        Assert.Equal(2, scope.Results.Count);
        Assert.Equal(1, scope.PassedCount);
        Assert.Equal(1, scope.FailureCount);
    }

    private static EvalResult EvalResultFixture() =>
        new(
            Metric: new("keyword", "Keyword", "quality", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0.0, false),
            EvaluatedAt: DateTimeOffset.Parse("2026-05-08T14:30:00Z"));
}
