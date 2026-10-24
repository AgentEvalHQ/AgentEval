// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using System.Text.Json;
using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.PartnerDeskDemo.Evals;

namespace AgentEval.PartnerDeskDemo.Tests;

[Collection("Console outcome")]
public sealed class EvalFinalizationTests(PartnerDeskPhaseFixture fixture) : IClassFixture<PartnerDeskPhaseFixture>
{
    [Fact]
    public async Task FinishReportAsync_RequestedArmInterrupted_KeepsIncompleteSelectionInTextAndJson()
    {
        var result = await FinishAsync([Arm(DemoPhase.Clean)], [DemoPhase.Clean, DemoPhase.Level1], cancelled: true);

        Assert.Equal(130, result.Exit);
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("gateArmVerdict").ValueKind);
        Assert.Contains("RESULT: INCOMPLETE", result.Text, StringComparison.Ordinal);
        Assert.Contains("retained", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Json.GetProperty("arms").GetArrayLength());
        Assert.True(result.Json.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task FinishReportAsync_AdmittedChecksCancelled_DoesNotReportSelfTestSuccess()
    {
        var phases = new[] { DemoPhase.Clean, DemoPhase.Compromised, DemoPhase.Level1, DemoPhase.Level2 };
        var result = await FinishAsync(phases.Select(Arm).ToArray(), phases, selfTest: true, cancelled: true);

        Assert.Equal(130, result.Exit);
        Assert.DoesNotContain("SELF-TEST PASS", result.Text, StringComparison.Ordinal);
        Assert.Contains("RESULT: CANCELLED", result.Text, StringComparison.Ordinal);
        Assert.True(result.Json.GetProperty("cancelled").GetBoolean());
        Assert.True(result.Json.GetProperty("gateArmVerdict").GetBoolean()); // Completed normal-arm evidence survives.
    }

    [Fact]
    public async Task FinishReportAsync_OnlyCleanRequested_PreservesCompletedSuccess()
    {
        var result = await FinishAsync([Arm(DemoPhase.Clean)], [DemoPhase.Clean]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("RESULT: PASS", result.Text, StringComparison.Ordinal);
        Assert.True(result.Json.GetProperty("gateArmVerdict").GetBoolean());
    }

    [Fact]
    public async Task FinishReportAsync_MissingRequestedArmWithoutCancellation_IsUnmeasured()
    {
        var result = await FinishAsync([Arm(DemoPhase.Clean)], [DemoPhase.Clean, DemoPhase.Level1]);
        Assert.Equal(11, result.Exit);
        Assert.Contains("RESULT: INCOMPLETE", result.Text, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("gateArmVerdict").ValueKind);
    }

    [Fact]
    public async Task FinishReportAsync_MissingCompromisedArm_DoesNotMislabelWholeReportAsComplete()
    {
        var result = await FinishAsync([Arm(DemoPhase.Clean)], [DemoPhase.Clean, DemoPhase.Compromised]);
        Assert.Equal(11, result.Exit);
        Assert.True(result.Json.GetProperty("gateArmVerdict").GetBoolean()); // This narrower predicate was measured.
        Assert.Contains("RESULT: INCOMPLETE", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishReportAsync_KnownFailureAndCancelledMissingArm_PreservesFalseAndExplicitCancellation()
    {
        var failed = new ArmResult(DemoPhase.Level1,
            [RunMetrics.From(fixture.Outcomes[DemoPhase.Level1]) with { OracleHeld = false }], [null]);
        var result = await FinishAsync([failed], [DemoPhase.Clean, DemoPhase.Level1], cancelled: true);
        Assert.Equal(130, result.Exit);
        Assert.False(result.Json.GetProperty("gateArmVerdict").GetBoolean());
        Assert.Contains("RESULT: CHECK", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task FinishReportAsync_CompletedSelfTest_KeepsSuccessAndFailure(bool failedCheck, int expected)
    {
        var phases = new[] { DemoPhase.Clean, DemoPhase.Compromised, DemoPhase.Level1, DemoPhase.Level2 };
        var result = await FinishAsync(phases.Select(Arm).ToArray(), phases, selfTest: true,
            checkFailures: failedCheck ? ["planted assertion control"] : []);
        Assert.Equal(expected, result.Exit);
        Assert.Contains(failedCheck ? "SELF-TEST FAIL" : "SELF-TEST PASS", result.Text, StringComparison.Ordinal);
    }

    private ArmResult Arm(DemoPhase phase) => new(phase, [RunMetrics.From(fixture.Outcomes[phase])], [null]);

    private static async Task<(int Exit, string Text, JsonElement Json)> FinishAsync(
        IReadOnlyList<ArmResult> completed,
        IReadOnlyList<DemoPhase> requested,
        bool selfTest = false,
        bool cancelled = false,
        IReadOnlyList<string>? checkFailures = null)
    {
        // Actual Main calls this same private finalization owner. No OS-specific signal or second verdict policy.
        var program = typeof(PartnerDeskEvaluator).Assembly.GetType("AgentEval.PartnerDeskDemo.Evals.Program", true)!;
        var method = program.GetMethod("FinishReportAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var path = Path.Combine(Path.GetTempPath(), $"t54-finalize-{Guid.NewGuid():N}.json");
        var stdout = Console.Out;
        var stderr = Console.Error;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var task = (Task<int>)method.Invoke(null,
                [new EvalRun("scripted finalization control", 1, DateTimeOffset.UnixEpoch, completed),
                    requested, selfTest, cancelled, checkFailures ?? [], path])!;
            var exit = await task;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            return (exit, writer.ToString(), json.RootElement.Clone());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            File.Delete(path);
            writer.Dispose();
        }
    }
}
