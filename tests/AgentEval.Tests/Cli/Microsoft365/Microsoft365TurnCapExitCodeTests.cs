// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Tests.Microsoft365.Agent;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// The exit code when <c>--m365-max-turns</c> stops a run. The cap throws
/// <see cref="WorkIqTurnBudgetExceededException"/> (a <c>FatalEvaluationException</c>) before the send that would cross
/// it; the run stops once instead of repeating the error per remaining item, and it never ends as a pass. Driven by a
/// REAL <see cref="WorkIqEvaluableAgent"/> (its own cap) over a scripted conversation whose turns complete.
/// </summary>
[Collection("ConsoleTests")]
public sealed class Microsoft365TurnCapExitCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-m365-cap-" + Guid.NewGuid().ToString("N"));

    public Microsoft365TurnCapExitCodeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task BenchGdpr_TurnCapReached_StopsWithExit1AndAnAbortedRun()
    {
        var (agent, conversation) = CappedAgent(maxTurns: 1);
        await using (agent)
        {
            await new AgentEval.Output.FileSystemOutputStore(Path.Combine(_root, ".agenteval")).InitializeSolutionAsync("m365-cap");
            var aef = new DirectoryInfo(Path.Combine(_root, "aef"));

            var exit = await BenchCommand.RunGdprAsync("standard", "m365-subject", _root, "question", new PassingEvaluator(), agentOverride: agent, aef: aef);

            Assert.Equal(1, exit);   // a stop, never a pass (0) and never a verdict
            Assert.Equal(1, conversation.Turns.Count);   // the cap refused the second send before anything went out
            Assert.Equal("aborted", (string?)RunJson(aef)["status"]);
        }
    }

    [Fact]
    public async Task Eval_TurnCapReached_PropagatesTheBudgetStopOnce()
    {
        var (agent, conversation) = CappedAgent(maxTurns: 1);
        await using (agent)
        {
            var dataset = Path.Combine(_root, "dataset.yaml");
            File.WriteAllText(dataset, "- id: c1\n  input: \"one\"\n  expectedOutput: \"x\"\n- id: c2\n  input: \"two\"\n  expectedOutput: \"x\"\n- id: c3\n  input: \"three\"\n  expectedOutput: \"x\"\n");
            var config = Path.Combine(_root, "m365.json");
            File.WriteAllText(config, "{ \"tenantId\": \"11111111-1111-1111-1111-111111111111\", \"appClientId\": \"22222222-2222-2222-2222-222222222222\" }");
            var opts = new EvalOptions
            {
                Dataset = new FileInfo(dataset),
                Sut = Microsoft365SutTarget.SutName,
                AckLiveSideEffects = true,
                TargetOptions = new Dictionary<string, ISutTargetOptions?>
                {
                    [Microsoft365SutTarget.SutName] = new Microsoft365SutOptions { ConfigFile = new FileInfo(config), MaxTurns = 1 },
                },
                Format = "json",
                Output = new FileInfo(Path.Combine(_root, "report.json")),
                Quiet = true,
            };

            // The cap propagates out of the run (the harness rethrows a FatalEvaluationException instead of turning it
            // into one error row per remaining case); the eval command's action maps it to exit 3 (RuntimeError).
            var ex = await Assert.ThrowsAsync<WorkIqTurnBudgetExceededException>(() => EvalCommand.ExecuteAsync(opts, CancellationToken.None, sutOverride: agent));

            Assert.Equal(1, ex.MaxTurns);
            Assert.Equal(1, ex.TurnsUsed);
            Assert.Equal(1, conversation.Turns.Count);   // stopped once: no send after the cap, no repeated error per case
            Assert.False(File.Exists(opts.Output!.FullName) && File.ReadAllText(opts.Output.FullName).Contains("\"passed\": true", StringComparison.Ordinal));
        }
    }

    private static (WorkIqEvaluableAgent Agent, FakeWorkIqConversation Conversation) CappedAgent(int maxTurns)
    {
        var conversation = new FakeWorkIqConversation();
        var agent = Microsoft365AgentFactory.FromConversation(conversation, "m365-capped", new WorkIqOptions { MaxTurns = maxTurns });
        return (agent, conversation);
    }

    private static JsonNode RunJson(DirectoryInfo aef) =>
        JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(aef.FullName, "run.json", SearchOption.AllDirectories))))!;

    private sealed class PassingEvaluator : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "test",
                CriteriaResults = criteria.Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "test" }).ToList(),
            });
    }
}
