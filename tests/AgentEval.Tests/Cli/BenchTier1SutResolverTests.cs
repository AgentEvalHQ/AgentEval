// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Track 2 PR3 + Part C (<c>strategy/CLI-Custom-Benchmarks-CopilotStudio-OpenAI-and-Metrics-Remediation-Design.md</c>
/// §2 parts B/C): <c>BenchTier1SutResolver</c> — the pure resolution logic behind <c>bench owasp</c>/<c>mitre</c>/
/// <c>nist</c>'s new <c>--sut</c> and <c>--endpoint</c>/<c>--model</c>/<c>--api-key</c> options. Tested directly
/// rather than through <c>Program.cs</c> (top-level statements aren't a unit-testable surface).
/// </summary>
public class BenchTier1SutResolverTests
{
    private static readonly IReadOnlyDictionary<string, ISutTargetOptions?> NoTargetOptions =
        new Dictionary<string, ISutTargetOptions?>();

    // ── regression guard: today's behavior when neither --sut nor --endpoint is set ──

    [Fact]
    public void Resolve_NeitherSutNorEndpoint_ReturnsNullAgentNullError()
    {
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: null, NoTargetOptions, targets: [], endpoint: null, model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.Null(error);
    }

    // ── --endpoint / --model / --api-key (Part C) ──

    [Fact]
    public void Resolve_EndpointWithoutModel_ReturnsError()
    {
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: null, NoTargetOptions, targets: [], endpoint: "http://localhost:11434/v1", model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.NotNull(error);
        Assert.Contains("--model", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_EndpointWithModel_BuildsAnAgent()
    {
        // Contract test (design doc §4 "C"): --endpoint/--model/--api-key on bench builds the same KIND of
        // agent eval --endpoint does — the exact same EndpointFactory.CreateOpenAICompatible + AsEvaluableAgent
        // pattern, reused, not reinvented (BuildFromOpenAiEndpoint delegates directly to it).
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: null, NoTargetOptions, targets: [], endpoint: "http://localhost:11434/v1", model: "llama3", apiKey: "test-key", subject: "MyAgent");

        Assert.Null(error);
        Assert.NotNull(agent);
    }

    [Fact]
    public void BuildFromOpenAiEndpoint_ReturnsAnEvaluableAgent()
    {
        var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint("http://localhost:11434/v1", "llama3", null, "MySubject");
        Assert.NotNull(agent);
    }

    // ── --sut (Track 2 PR3): unknown target ──

    [Fact]
    public void Resolve_UnknownSut_ReturnsError()
    {
        var targets = SutTargetResolver.BuiltInTargets().Where(t => t.SupportedVerbs.Contains("bench")).ToList();
        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: "bogus", NoTargetOptions, targets, endpoint: null, model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.NotNull(error);
        Assert.Contains("copilot-studio", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── --sut copilot-studio: safety-gate validation surfaces as a friendly error, not an exception ──

    [Fact]
    public void Resolve_CopilotStudio_WithoutConsent_ReturnsError()
    {
        var targets = SutTargetResolver.BuiltInTargets().Where(t => t.SupportedVerbs.Contains("bench")).ToList();
        var targetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = null, AckLiveSideEffects = false, MaxCredits = 0 },
        };

        var (agent, error) = BenchTier1SutResolver.Resolve(
            sut: "copilot-studio", targetOptions, targets, endpoint: null, model: null, apiKey: null, subject: "MyAgent");

        Assert.Null(agent);
        Assert.NotNull(error);
        Assert.Contains("i-understand-live-side-effects", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── --sut copilot-studio: full credential-free resolution via sutOverride, through the REAL target ──

    [Fact]
    public void Resolve_CopilotStudio_BenignAgent_ResolvesOffline()
    {
        var targets = SutTargetResolver.BuiltInTargets().Where(t => t.SupportedVerbs.Contains("bench")).ToList();
        var cfg = WriteValidConfig();
        try
        {
            var targetOptions = new Dictionary<string, ISutTargetOptions?>
            {
                ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = cfg, AckLiveSideEffects = true, MaxCredits = 0 },
            };

            AIAgent inner = new ChatClientAgent(new AlwaysHelpfulChatClient(), new ChatClientAgentOptions { Name = "cs-fake" });
            IEvaluableAgent sut = CopilotStudioAgentFactory.FromAgent(inner);

            var (agent, error) = BenchTier1SutResolver.Resolve(
                sut: "copilot-studio", targetOptions, targets, endpoint: null, model: null, apiKey: null, subject: "MyAgent",
                sutOverride: sut);

            Assert.Null(error);
            Assert.Same(sut, agent);
        }
        finally { TryDelete(cfg); }
    }

    // ── helpers ──

    private static FileInfo WriteValidConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-bench-sut-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{ \"environmentId\": \"env-1\", \"schemaName\": \"cr1a2_agent\", \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

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
