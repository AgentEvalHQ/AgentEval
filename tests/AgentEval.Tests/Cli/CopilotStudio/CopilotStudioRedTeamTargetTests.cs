// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Stage 2 of the Copilot Studio MVP — the <c>--sut copilot-studio</c> built-in red-team target. Its safety gates
/// (consent, config-required, parallelism) run before any network call, and a full scan drives the whole path
/// credential-free via the <c>sutOverride</c> seam (a benign fake agent → no live connector, no creds).
/// </summary>
public class CopilotStudioRedTeamTargetTests
{
    // ── safety-gate validation (throws before any build/network) ──

    [Fact]
    public async Task Redteam_CopilotStudio_WithoutConsent_Refuses()
    {
        var opts = BaseOptions(ackConsent: false, config: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedTeamCommand.ExecuteAsync(opts, default));
        Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redteam_CopilotStudio_WithoutConfig_Refuses()
    {
        var opts = BaseOptions(ackConsent: true, config: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedTeamCommand.ExecuteAsync(opts, default));
        Assert.Contains("copilotstudio-config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redteam_CopilotStudio_ParallelismAboveOne_Refuses()
    {
        var f = WriteValidConfig();
        try
        {
            var opts = BaseOptions(ackConsent: true, config: f, parallelism: 2);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedTeamCommand.ExecuteAsync(opts, default));
            Assert.Contains("parallelism 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(f); }
    }

    [Fact]
    public async Task Redteam_UnknownSut_ListsCopilotStudio()
    {
        var opts = BaseOptions(ackConsent: true, config: null, sut: "bogus");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedTeamCommand.ExecuteAsync(opts, default));
        Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── credential-free end-to-end scan via the sutOverride seam ──

    [Fact]
    public async Task Redteam_CopilotStudio_BenignAgent_ScansCleanOffline()
    {
        var cfg = WriteValidConfig();
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-cs-report-" + Guid.NewGuid().ToString("N") + ".json"));
        try
        {
            // The SUT is built exactly the way production will (MAF AIAgent -> MAFAgentAdapter), over a benign fake
            // client — so the whole --sut copilot-studio path runs with zero credentials and zero network.
            AIAgent inner = new ChatClientAgent(new AlwaysBenignChatClient(), new ChatClientAgentOptions { Name = "cs-fake" });
            IEvaluableAgent sut = CopilotStudioAgentFactory.FromAgent(inner);

            var opts = BaseOptions(ackConsent: true, config: cfg, attacks: "PromptInjection", maxProbes: 1,
                intensity: "quick", output: outFile);

            var exit = await RedTeamCommand.ExecuteAsync(opts, default, sutOverride: sut);

            Assert.Equal(ExitCodes.Success, exit);           // benign agent resists → no vulnerability → exit 0
            Assert.True(outFile.Exists && outFile.Length > 0, "a report should have been written");
        }
        finally { TryDelete(cfg); TryDelete(outFile); }
    }

    // ── helpers ──

    private static RedTeamOptions BaseOptions(
        bool ackConsent, FileInfo? config, string? sut = "copilot-studio", int parallelism = 1,
        string? attacks = null, int maxProbes = 0, string intensity = "moderate", FileInfo? output = null) => new()
    {
        Sut = sut,
        TargetOptions = new Dictionary<string, IRedTeamTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioTargetOptions { ConfigFile = config, AckLiveSideEffects = ackConsent, MaxCredits = 0 },
        },
        Parallelism = parallelism,
        Attacks = attacks,
        MaxProbes = maxProbes,
        Intensity = intensity,
        Format = "json",
        FailOn = "vuln",
        Quiet = true,
        Output = output,
    };

    private static FileInfo WriteValidConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-cs-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{ \"environmentId\": \"env-1\", \"schemaName\": \"cr1a2_agent\", \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>A benign IChatClient that refuses every prompt, reusable across unlimited probes (no queue to exhaust).</summary>
    private sealed class AlwaysBenignChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I can't help with that request."))
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
