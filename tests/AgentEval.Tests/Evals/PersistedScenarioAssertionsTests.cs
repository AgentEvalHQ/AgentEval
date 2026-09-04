// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-030 Slice 0.7, the named acceptance test. AE-01 stopped the lie in
/// <c>ScenarioResult.Assertions</c> (it was hard-coded <c>Array.Empty</c> at both construction sites);
/// this pins the property §8 asks for — the field is honest: empty means nothing was recorded, and a
/// populated list is exactly what was recorded, outcome for outcome. The full AE-01 coverage (disk
/// round-trip, discrimination pair, exporter path) lives in <c>AssertionResultRecordingTests</c>.
/// </summary>
public class PersistedScenarioAssertionsTests
{
    private static EvalResult SomeResult() => new(
        Metric: new("k", "n", "c", "1.0.0"),
        Score: new(0.9, null, "pass", true, null, "none", null),
        Details: new(null, null, null, null, null),
        Provenance: new("atomic-code", null, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void PersistedScenario_AssertionsAreNotFabricated()
    {
        // (a) Nothing recorded, no scope → empty, and that emptiness is true.
        var none = EvalResultPersistence.ToScenarioResult(SomeResult(), "s1", "Scenario 1");
        Assert.Empty(none.Assertions);

        // (b) Three recorded → three persisted, with the recorded outcomes — not a list the exporter made up,
        //     and never an undecidable rendered as a pass.
        using (AgentEvalScope.Collecting())
        {
            AgentEvalScope.RecordPass("HaveCalledTool(Search)");
            AgentEvalScope.Record(AssertionResult.Fail("HaveCallCount", "Expected 2 tool call(s), but 3 were made."));
            AgentEvalScope.RecordInconclusive("NeverCallTool(PlaceOrder)", "tool inventory was not declared");

            var some = EvalResultPersistence.ToScenarioResult(SomeResult(), "s2", "Scenario 2");

            Assert.Equal(3, some.Assertions.Count);
            Assert.Equal(1, some.Assertions.Count(a => a.Outcome == AssertionOutcome.Passed));
            Assert.Equal(1, some.Assertions.Count(a => a.Outcome == AssertionOutcome.Failed));
            Assert.Equal(1, some.Assertions.Count(a => a.Outcome == AssertionOutcome.Inconclusive));
            Assert.DoesNotContain(some.Assertions, a => a.IsInconclusive && a.Passed);
            Assert.Contains(some.Assertions, a => a.Assertion == "HaveCalledTool(Search)" && a.Passed);
        }

        // (c) An explicit list is persisted verbatim — an explicit empty list wins over the ambient scope.
        using (AgentEvalScope.Collecting())
        {
            AgentEvalScope.RecordPass("x");
            var explicitEmpty = EvalResultPersistence.ToScenarioResult(SomeResult(), "s3", "Scenario 3", Array.Empty<AssertionResult>());
            Assert.Empty(explicitEmpty.Assertions);
        }
    }
}
