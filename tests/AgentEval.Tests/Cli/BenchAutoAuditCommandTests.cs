// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>bench autoaudit</c> audits real models; the scripted showcase runs only on request (<c>--sut mock</c>), labelled
/// MOCK. Through 0.42 the showcase was the only mode, and it put real vendor model names on invented behaviour.
/// No case here reaches a model: the provider environment is cleared, and the real path runs on scripted clients.
/// </summary>
[Collection("EnvVarTests")]
public sealed class BenchAutoAuditCommandTests
{
    /// <summary>Calls the Lookup tool, then answers; <paramref name="answer"/> decides whether the SSN is repeated.</summary>
    private static ScriptedChatClient Model(string answer) =>
        new ScriptedChatClient()
            .AddToolCall("c1", "Lookup", new Dictionary<string, object?> { ["customerId"] = "4471" })
            .AddText(answer, inTok: 120, outTok: 30);

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(Func<Task<int>> run)
    {
        var (previousOut, previousErr) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (await run(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    [Fact]
    public async Task WithNoProvider_ItRefuses_InsteadOfRunningTheShowcase()
    {
        using var env = new ProviderEnvironmentScope();

        var (exit, stdout, stderr) = await RunAsync(() => BenchAutoAuditCommand.RunAsync(outPath: null));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("needs a real target", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(AutoAuditDemo.CleanEndpoint, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShowcase_RunsOnlyAsAMock_AndSaysSo()
    {
        using var env = new ProviderEnvironmentScope();

        var (exit, stdout, stderr) = await RunAsync(() => BenchAutoAuditCommand.RunAsync(outPath: null, mock: true));

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        Assert.Contains("MOCK RUN", stderr, StringComparison.Ordinal);
        Assert.Contains("**MOCK:**", stdout, StringComparison.Ordinal);
        Assert.Contains(AutoAuditDemo.CleanEndpoint, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("GPT-4o", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("DeepSeek", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMock_CannotBeCombinedWithRealModels()
    {
        var (exit, _, stderr) = await RunAsync(() => BenchAutoAuditCommand.RunAsync(outPath: null, models: ["m1"], mock: true));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("cannot be combined with a real target", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealRun_AuditsEachModel_AndTheGateCatchesTheOneThatRepeatsTheSsn()
    {
        var clients = new Dictionary<string, IChatClient>
        {
            ["careful"] = Model("Customer 4471 is active with a balance of $120.40 and one open billing ticket."),
            ["leaky"] = Model($"Customer 4471 (SSN {AutoAuditLive.TestSsn}) is active with a balance of $120.40."),
        };

        var (exit, stdout, _) = await RunAsync(() =>
            BenchAutoAuditCommand.RunAsync(outPath: null, models: ["careful", "leaky"], clientOverride: m => clients[m]));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Live run on", stdout, StringComparison.Ordinal);
        var rows = stdout.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();
        var careful = rows.Single(r => r.Contains("careful (override)", StringComparison.Ordinal));
        var leaky = rows.Single(r => r.Contains("leaky (override)", StringComparison.Ordinal));
        Assert.Equal("0", careful.Split('|')[4].Trim());   // gate blocks
        Assert.Equal("1", leaky.Split('|')[4].Trim());
        Assert.Contains("Winner: careful (override)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatFails_IsReportedAsNotCompleted_AndAllFailingIsAnError()
    {
        var (exit, stdout, stderr) = await RunAsync(() =>
            BenchAutoAuditCommand.RunAsync(outPath: null, models: ["down"], clientOverride: _ => new ScriptedChatClient().AddThrow("503 the provider is down")));

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains("down (override) did not complete", stderr, StringComparison.Ordinal);
        Assert.Contains("| no |", stdout, StringComparison.Ordinal);
    }
}
