// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using AgentEval.MAF.CopilotStudio;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Track 2 PR2 (<c>strategy/CopilotStudio/Bench-Eval-Integration-and-Live-Connector-Plan.md</c> §3.4 "eval"):
/// <c>eval --sut copilot-studio</c> — the shared <c>ISutTarget</c> seam reused by a SECOND verb, without
/// touching <c>RedTeamCommand</c>/<c>RedTeamOptions</c>. Mirrors <c>CopilotStudioRedTeamTargetTests</c>'
/// coverage shape (safety-gate validation, then a full credential-free scan via <c>sutOverride</c>).
/// </summary>
public class EvalCommandCopilotStudioSutTests
{
    // ── safety-gate validation (throws before any build/network) ──

    [Fact]
    public async Task Eval_CopilotStudio_WithoutConsent_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: false, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_CopilotStudio_WithoutConfig_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("copilotstudio-config", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_UnknownSut_Refuses()
    {
        var dataset = CreateTempDataset();
        try
        {
            var opts = BaseOptions(dataset, ackConsent: true, config: null, sut: "bogus");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EvalCommand.ExecuteAsync(opts, default));
            Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_MissingDataset_ThrowsBeforeSutValidation()
    {
        // Regression guard: the dataset-existence check must still run first for a --sut scan specifically
        // (a bad --sut config shouldn't mask a typo'd --dataset path, and vice versa — this proves
        // Dataset.Exists is checked before ISutTarget.Validate). This precedence is --sut-ONLY, not shared
        // with the classic (--endpoint/--azure) path — that path's OWN, deliberately different precedence
        // (connection-config validation before dataset-existence) is covered separately by
        // EvalCommandTests.ExecuteAsync_ClassicPath_MissingDatasetAndNoConnectionConfig_ThrowsEndpointErrorFirst
        // (review: an earlier version of this comment incorrectly claimed the classic path already matched
        // this ordering — it didn't, and applying --sut's dataset-first requirement to the classic path too
        // was itself a regression, since fixed).
        var opts = BaseOptions(new FileInfo("/nonexistent/path/to/dataset.yaml"), ackConsent: false, config: null);
        await Assert.ThrowsAsync<FileNotFoundException>(() => EvalCommand.ExecuteAsync(opts, default));
    }

    // ── credential-free end-to-end eval via the sutOverride seam ──

    [Fact]
    public async Task Eval_CopilotStudio_BenignAgent_RunsCleanOffline()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            // The SUT is built exactly the way production will (MAF AIAgent -> MAFAgentAdapter), over a
            // benign fake client — so the whole `eval --sut copilot-studio` path runs with zero credentials.
            AIAgent inner = new ChatClientAgent(new AlwaysHelpfulChatClient(), new ChatClientAgentOptions { Name = "cs-fake" });
            IEvaluableAgent sut = CopilotStudioAgentFactory.FromAgent(inner);

            var opts = BaseOptions(dataset, ackConsent: true, config: cfg);

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: sut);

            Assert.Equal(ExitCodes.Success, exit);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    // ── --metrics + --runs > 1 (review): must warn, never throw, even with no evaluator client ──

    [Fact]
    public async Task Eval_CopilotStudio_RunsGreaterThan1_WithMetricsAndNoJudge_WarnsInsteadOfThrowing()
    {
        // Regression guard: --metrics used to be resolved to real IMetric instances BEFORE the --runs > 1
        // check ran, so `--sut copilot-studio --runs 5 --metrics llm_relevance` (no --judge, and a --sut
        // target exposes no raw IChatClient to fall back to) threw inside MetricCatalog.Resolve instead of
        // reaching the graceful "--metrics has no effect combined with --runs > 1" warning-and-ignore path
        // (--metrics scoring isn't wired to the stochastic path at all, so the instances would never even
        // have been used). This must complete successfully, not throw.
        var dataset = CreateTempDataset();
        var cfg = WriteValidConfig();
        try
        {
            AIAgent inner = new ChatClientAgent(new AlwaysHelpfulChatClient(), new ChatClientAgentOptions { Name = "cs-fake" });
            IEvaluableAgent sut = CopilotStudioAgentFactory.FromAgent(inner);

            var opts = BaseOptions(dataset, ackConsent: true, config: cfg, runs: 5, metrics: "llm_relevance");

            var exit = await EvalCommand.ExecuteAsync(opts, default, sutOverride: sut);

            // The stochastic path's own pass/fail depends on StochasticRunner's threshold logic, not on
            // --metrics at all — the point of this test is that it completes (any exit code), not throws.
            Assert.True(exit is ExitCodes.Success or ExitCodes.TestFailure);
        }
        finally { TryDelete(dataset); TryDelete(cfg); }
    }

    // ── helpers ──

    private static EvalOptions BaseOptions(
        FileInfo dataset, bool ackConsent, FileInfo? config, string? sut = "copilot-studio", int runs = 1, string? metrics = null) => new()
    {
        Dataset = dataset,
        Sut = sut,
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = config, AckLiveSideEffects = ackConsent, MaxCredits = 0 },
        },
        Format = "json",
        Quiet = true,
        Runs = runs,
        Metrics = metrics,
    };

    private static FileInfo CreateTempDataset()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-cs-dataset-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        return new FileInfo(path);
    }

    private static FileInfo WriteValidConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-eval-cs-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{ \"environmentId\": \"env-1\", \"schemaName\": \"cr1a2_agent\", \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>A benign IChatClient with a fixed helpful reply, reusable across unlimited eval calls.</summary>
    private sealed class AlwaysHelpfulChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "cs-fake",
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents)
                {
                    FinishReason = response.FinishReason,
                    ModelId = response.ModelId,
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
