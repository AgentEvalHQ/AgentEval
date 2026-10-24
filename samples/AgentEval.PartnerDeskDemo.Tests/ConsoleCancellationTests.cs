// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using AgentEval.PartnerDeskDemo.Demo;

namespace AgentEval.PartnerDeskDemo.Tests;

[CollectionDefinition("Console outcome", DisableParallelization = true)]
public sealed class ConsoleOutcomeCollection;

[Collection("Console outcome")]
public sealed class ConsoleCancellationTests
{
    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 11)]
    [InlineData(true, true, 1)]
    public async Task RunAndReportAsync_CancellationOrigin_PreservesRuntimeErrorAndStickyFailure(
        bool callerCancelled, bool priorFailure, int expected)
    {
        // Exercise the existing console consumer without adding a production test-only API.
        var program = typeof(AgentEval.PartnerDeskDemo.Program);
        var state = program.Assembly.GetType("AgentEval.Samples.SampleOutcome", throwOnError: true)!;
        void Invoke(string method, params object[] args) => state
            .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        Invoke("Reset");
        var outbox = Path.Combine(Path.GetTempPath(), $"t54-cancel-{Guid.NewGuid():N}.log");
        try
        {
            if (priorFailure)
                Invoke("Fail", "previous measured failure");
            using var cancellation = new CancellationTokenSource();
            if (callerCancelled)
                cancellation.Cancel();
            await using var runner = new PartnerDeskRunner(
                _ => throw new OperationCanceledException("internal provider timeout"), DemoOutput.Silent, outbox);
            var task = (Task)program.GetMethod("RunAndReportAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [runner, DemoPhase.Clean, AgentEval.PartnerDeskDemo.Program.StandardQuestion,
                    DemoOutput.Silent, cancellation.Token])!;
            await task;

            Assert.Equal(expected, (int)state.GetProperty("ExitCode")!.GetValue(null)!);
        }
        finally
        {
            Invoke("Reset");
            File.Delete(outbox);
        }
    }
}
