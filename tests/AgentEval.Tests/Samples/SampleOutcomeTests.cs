// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Samples;
using Xunit;

namespace AgentEval.Tests.Samples;

/// <summary>
/// The samples' exit-code contract (<c>samples/AgentEval.Samples/SampleOutcome.cs</c>, compiled here as shipped): what a
/// run records becomes its exit code, the worst record wins, and nothing recorded is "completed", never "passed".
/// SampleOutcome is process-wide state, so the class runs in the serial console collection and resets around each test.
/// </summary>
[Collection("ConsoleTests")]
public sealed class SampleOutcomeTests : IDisposable
{
    public SampleOutcomeTests() => SampleOutcome.Reset();

    public void Dispose() => SampleOutcome.Reset();

    [Fact]
    public void NothingRecorded_IsCompleted()
    {
        Assert.Equal(SampleOutcome.Completed, SampleOutcome.ExitCode);
        Assert.Empty(SampleOutcome.Records);
    }

    [Theory]
    [InlineData("warn", SampleOutcome.Warning)]
    [InlineData("warn,skip", SampleOutcome.NotMeasured)]
    [InlineData("skip,warn", SampleOutcome.NotMeasured)]
    [InlineData("skip,fail", SampleOutcome.Failed)]
    [InlineData("fail,skip,warn", SampleOutcome.Failed)]
    [InlineData("warn,fail,error", SampleOutcome.RuntimeError)]
    [InlineData("error,fail", SampleOutcome.RuntimeError)]
    public void TheWorstRecordWins_ErrorThenFailThenNotMeasuredThenWarn(string records, int expected)
    {
        foreach (var record in records.Split(','))
        {
            switch (record)
            {
                case "warn": SampleOutcome.Warn("a score under its threshold"); break;
                case "skip": SampleOutcome.Skip("a provider is missing"); break;
                case "fail": SampleOutcome.Fail("a check did not hold"); break;
                case "error": SampleOutcome.Error("a provider call threw"); break;
            }
        }

        Assert.Equal(expected, SampleOutcome.ExitCode);
        Assert.Equal(records.Split(',').Length, SampleOutcome.Records.Count);
    }

    [Theory]
    [InlineData(SampleOutcome.Completed, SampleOutcome.Warning, SampleOutcome.Warning)]
    [InlineData(SampleOutcome.NotMeasured, SampleOutcome.Warning, SampleOutcome.NotMeasured)]
    [InlineData(SampleOutcome.NotMeasured, SampleOutcome.Failed, SampleOutcome.Failed)]
    [InlineData(SampleOutcome.Failed, SampleOutcome.RuntimeError, SampleOutcome.RuntimeError)]
    [InlineData(SampleOutcome.Completed, SampleOutcome.Completed, SampleOutcome.Completed)]
    public void Worse_KeepsTheMoreSevereCode_InEitherOrder(int a, int b, int expected)
    {
        // Running several samples from the menu folds each run's code into the process exit code with Worse.
        Assert.Equal(expected, SampleOutcome.Worse(a, b));
        Assert.Equal(expected, SampleOutcome.Worse(b, a));
    }

    [Fact]
    public void Reset_ForgetsThePreviousRun()
    {
        SampleOutcome.Fail("the previous sample failed");

        SampleOutcome.Reset();

        Assert.Equal(SampleOutcome.Completed, SampleOutcome.ExitCode);
        Assert.Empty(SampleOutcome.Records);
    }

    [Fact]
    public void Describe_NamesTheResultTheCodeAndEveryReason()
    {
        SampleOutcome.Skip("no chat provider configured");
        SampleOutcome.Warn("similarity 40% is below the 50% threshold");

        var lines = SampleOutcome.Describe(SampleOutcome.ExitCode);

        Assert.Equal("Sample result: NOT MEASURED (exit 11)", lines[0]);
        Assert.Contains(lines, l => l.Contains("NOT MEASURED: no chat provider configured", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("WARN: similarity 40% is below the 50% threshold", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_ACompletedRun_SaysNothingWasRecorded_NotThatItPassed()
    {
        var line = Assert.Single(SampleOutcome.Describe(SampleOutcome.Completed));

        Assert.Equal("Sample result: COMPLETED (no failed check, warning or skip recorded) (exit 0)", line);
        Assert.DoesNotContain("PASS", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_AMockRun_IsLabelledAsNotAMeasurement()
    {
        var line = Assert.Single(SampleOutcome.Describe(SampleOutcome.Completed, mock: true));

        Assert.Contains("mock run: canned replies, not a measurement", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AReason_IsOneLine_AndARunawayOneIsCapped()
    {
        SampleOutcome.Fail("Workflow assertion failed (2 issue(s))\r\n  • Expected 4 steps\n  • Order mismatch");
        SampleOutcome.Fail(new string('x', 1000));

        var (_, first) = SampleOutcome.Records[0];
        var (_, second) = SampleOutcome.Records[1];
        Assert.Equal("Workflow assertion failed (2 issue(s)) • Expected 4 steps • Order mismatch", first);
        Assert.True(second.Length <= 300, $"a reason is capped at 300 characters, got {second.Length}");
        Assert.EndsWith("…", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankReason_IsRefused(string reason)
    {
        Assert.ThrowsAny<ArgumentException>(() => SampleOutcome.Fail(reason));
        Assert.ThrowsAny<ArgumentException>(() => SampleOutcome.Skip(reason));
    }

    [Theory]
    [InlineData("pass", true, SampleOutcome.Completed)]
    [InlineData("fail", false, SampleOutcome.Failed)]
    [InlineData("warn", true, SampleOutcome.Warning)]
    [InlineData("error", false, SampleOutcome.NotMeasured)]
    [InlineData("skipped", false, SampleOutcome.NotMeasured)]
    public void RecordVerdict_ReadsABenchmarkRootTheWayEveryReportDoes(string label, bool passed, int expected)
    {
        // A root that errored or measured nothing is not measured: never a pass, and never a zero read as a fail.
        var root = new EvalScore(0.4, null, label, passed, null, "none", null);

        SampleOutcome.RecordVerdict("a benchmark", root);

        Assert.Equal(expected, SampleOutcome.ExitCode);
    }

    [Theory]
    [InlineData("skipped", SampleOutcome.NotMeasured)]
    [InlineData("error", SampleOutcome.NotMeasured)]
    [InlineData("inapplicable", SampleOutcome.Completed)]
    [InlineData("pass", SampleOutcome.Completed)]
    public void RecordVerdict_OnATree_APassingRootOverAnUntestedLeaf_IsNotMeasured(string leafLabel, int expected)
    {
        // A weighted root skips a skipped leaf, so a provider that rejected one probe still left the root PASS and the
        // run exited 0 over a probe it never tested. Not applicable by design is not a gap.
        static EvalResult Node(string label, bool passed, params EvalResult[] children) => new(
            new EvalMetadata(label, $"check {label}", "test", "1.0.0"),
            new EvalScore(passed ? 1 : 0, null, label, passed, null, "none", null),
            new EvalDetails(null, null, null, children.Length > 0 ? children : null, null),
            new EvalProvenance("code", null, null, null, null, 0, false),
            DateTimeOffset.UnixEpoch);
        var tree = Node("pass", true, Node("pass", true), Node(leafLabel, leafLabel == "pass"));

        SampleOutcome.RecordVerdict("a benchmark", tree);

        Assert.Equal(expected, SampleOutcome.ExitCode);
        if (expected == SampleOutcome.NotMeasured)
            Assert.Contains("1 of 2 checks were not measured", SampleOutcome.Records.Single().Reason);
    }

    [Fact]
    public void ACheckFailure_IsStillAnInvalidOperationException_SoOldCatchSitesKeepWorking()
    {
        // Gatekeeper samples throw it where they threw InvalidOperationException; the runner maps it to FAIL (exit 1).
        Exception thrown = new SampleCheckFailedException("Bulkhead sample failed: saturation escaped.");

        Assert.IsAssignableFrom<InvalidOperationException>(thrown);
    }
}
