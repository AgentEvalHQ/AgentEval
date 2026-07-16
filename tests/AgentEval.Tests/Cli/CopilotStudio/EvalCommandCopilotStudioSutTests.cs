// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
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
        // Regression guard: the dataset-existence check must still run first, even for a --sut scan
        // (a bad --sut config shouldn't mask a typo'd --dataset path, and vice versa — this proves
        // Dataset.Exists is checked before ISutTarget.Validate, matching the pre-existing endpoint path).
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

    // ── helpers ──

    private static EvalOptions BaseOptions(
        FileInfo dataset, bool ackConsent, FileInfo? config, string? sut = "copilot-studio") => new()
    {
        Dataset = dataset,
        Sut = sut,
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = config, AckLiveSideEffects = ackConsent, MaxCredits = 0 },
        },
        Format = "json",
        Quiet = true,
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
