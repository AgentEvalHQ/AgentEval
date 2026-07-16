// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Track 2 (Bench-Eval-Integration-and-Live-Connector-Plan.md §3, PR 1) — the shared --sut seam for
// eval/bench. Proves (a) SutTargetResolver's dispatch mechanics and (b) the "Validate-drift" contract:
// IRedTeamBuiltInTarget.Validate and ISutTarget.Validate must agree on the same accept/reject truth table
// for the checks they SHARE (consent, config-required, max-credits >= 0) — the one real ongoing-sync cost
// the design doc calls out, since the two method bodies have no compiler-enforced sync.
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

public class SutTargetResolverTests
{
    // ── SutTargetResolver dispatch mechanics ──

    [Fact]
    public void BuiltInTargets_IncludesCopilotStudio()
    {
        var targets = SutTargetResolver.BuiltInTargets();
        Assert.Contains(targets, t => t.Sut == "copilot-studio");
    }

    [Fact]
    public void CopilotStudioTarget_SupportsEvalAndBench_NotRedteam()
    {
        // gatekeeper-demo stays redteam-only (needs AgentTrace); copilot-studio is the one target that
        // DOES cross verbs — but "redteam" itself still goes through IRedTeamBuiltInTarget, not this seam.
        var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "copilot-studio");
        Assert.Contains("eval", target.SupportedVerbs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("bench", target.SupportedVerbs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_NoSutSupplied_ReturnsNullAgent_CallerFallsBackToEndpointAzure()
    {
        var opts = new CommonTargetOptions();
        var targets = SutTargetResolver.BuiltInTargets();

        var (agent, name) = SutTargetResolver.TryResolve(opts, targets, sutOverride: null);

        Assert.Null(agent);
        Assert.Null(name);
    }

    [Fact]
    public void TryResolve_UnknownSut_ThrowsListingValidTargets()
    {
        var opts = new CommonTargetOptions { Sut = "totally-bogus" };
        var targets = SutTargetResolver.BuiltInTargets();

        var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, targets, sutOverride: null));
        Assert.Contains("copilot-studio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_CopilotStudio_WithSutOverride_ReturnsOverrideCredentialFree()
    {
        var configFile = WriteValidConfig();
        try
        {
            var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "copilot-studio");
            var opts = new CommonTargetOptions
            {
                Sut = "copilot-studio",
                TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = configFile, AckLiveSideEffects = true, MaxCredits = 0 },
                },
            };

            AIAgent inner = new ChatClientAgent(new AlwaysBenignChatClient(), new ChatClientAgentOptions { Name = "sut-fake" });
            IEvaluableAgent fakeAgent = CopilotStudioAgentFactory.FromAgent(inner);
            var (agent, name) = SutTargetResolver.TryResolve(opts, [target], sutOverride: fakeAgent);

            Assert.Same(fakeAgent, agent);   // the test seam: sutOverride wins, no live connector touched
            Assert.NotNull(name);
        }
        finally
        {
            TryDelete(configFile);
        }
    }

    [Fact]
    public void TryResolve_CopilotStudio_WithoutConsent_Refuses_BeforeAnyBuild()
    {
        var configFile = WriteValidConfig();
        try
        {
            var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "copilot-studio");
            var opts = new CommonTargetOptions
            {
                Sut = "copilot-studio",
                TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = configFile, AckLiveSideEffects = false },
                },
            };

            var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, [target], sutOverride: null));
            Assert.Contains("i-understand-live-side-effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(configFile);
        }
    }

    [Fact]
    public void TryResolve_CopilotStudio_WithoutConfig_Refuses()
    {
        var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "copilot-studio");
        var opts = new CommonTargetOptions
        {
            Sut = "copilot-studio",
            TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
            {
                ["copilot-studio"] = new CopilotStudioSutOptions { AckLiveSideEffects = true },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, [target], sutOverride: null));
        Assert.Contains("copilotstudio-config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_CopilotStudio_NegativeMaxCredits_Refuses()
    {
        var configFile = WriteValidConfig();
        try
        {
            var target = SutTargetResolver.BuiltInTargets().Single(t => t.Sut == "copilot-studio");
            var opts = new CommonTargetOptions
            {
                Sut = "copilot-studio",
                TargetOptions = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = configFile, AckLiveSideEffects = true, MaxCredits = -1 },
                },
            };

            var ex = Assert.Throws<InvalidOperationException>(() => SutTargetResolver.TryResolve(opts, [target], sutOverride: null));
            Assert.Contains("max-credits", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(configFile);
        }
    }

    // ── Validate-drift contract: IRedTeamBuiltInTarget.Validate vs ISutTarget.Validate must agree on
    //    accept/reject for the SHARED checks (consent / config-required / max-credits >= 0). ──

    [Theory]
    [InlineData(true, true, 0, true)]     // consent + config + valid credits -> both accept
    [InlineData(false, true, 0, false)]   // no consent -> both refuse
    [InlineData(true, false, 0, false)]   // no config -> both refuse
    [InlineData(true, true, -1, false)]   // negative credits -> both refuse
    public void ValidateDrift_RedTeamAndSutPaths_AgreeOnAcceptReject(bool consent, bool hasConfig, int maxCredits, bool expectAccept)
    {
        var configFile = hasConfig ? WriteValidConfig() : null;
        try
        {
            // ── redteam path ──
            var redteamTarget = new CopilotStudioRedTeamTarget();
            var redteamOpts = new RedTeamOptions
            {
                Sut = "copilot-studio",
                Parallelism = 1,
                TargetOptions = new Dictionary<string, IRedTeamTargetOptions?>
                {
                    ["copilot-studio"] = new CopilotStudioTargetOptions { ConfigFile = configFile, AckLiveSideEffects = consent, MaxCredits = maxCredits },
                },
                Intensity = "quick",
                Format = "json",
            };
            var redteamAccepted = TryValidate(() => redteamTarget.Validate(redteamOpts));

            // ── shared eval/bench path ──
            var sutTarget = new CopilotStudioRedTeamTarget();
            ISutTargetOptions sutOwn = new CopilotStudioSutOptions { ConfigFile = configFile, AckLiveSideEffects = consent, MaxCredits = maxCredits };
            var sutAccepted = TryValidate(() => ((ISutTarget)sutTarget).Validate(new CommonTargetOptions { Sut = "copilot-studio" }, sutOwn));

            Assert.Equal(expectAccept, redteamAccepted);
            Assert.Equal(expectAccept, sutAccepted);
            Assert.Equal(redteamAccepted, sutAccepted);   // the actual drift-guard assertion
        }
        finally
        {
            if (configFile is not null)
            {
                TryDelete(configFile);
            }
        }
    }

    private static bool TryValidate(Action validate)
    {
        try
        {
            validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static FileInfo WriteValidConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-sut-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{ \"environmentId\": \"env-1\", \"schemaName\": \"cr1a2_agent\", \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private sealed class AlwaysBenignChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "benign reply"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "cs-fake",
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
