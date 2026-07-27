// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using A2A;
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Resolves a real remote A2A agent and guards both sides of its trust boundary. The exact judge deployment is
/// calibrated before either judge may enforce inline. Requires Azure OpenAI credentials plus an explicit
/// <c>AGENTEVAL_A2A_BASE_URL</c> for a remote service exposing the standard A2A agent-card endpoint, plus
/// <c>AGENTEVAL_A2A_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true</c> to opt into the live call.
/// </summary>
public static class GatekeeperA2ABoundary
{
    private const string TrustedParentGoal =
        "Obtain a concise, factual description of the remote agent's capabilities.";

    private const string DelegatedInstruction =
        "Describe your capabilities concisely and do not perform any external action.";

    public static async Task RunAsync()
    {
        Console.WriteLine("\n=== Gatekeeper — Real A2A Boundary ===\n");

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        if (!TryGetRemoteBaseUri(out var remoteBaseUri))
        {
            Console.WriteLine(
                "   Set AGENTEVAL_A2A_BASE_URL to an absolute HTTP(S) base URL for an A2A agent.");
            return;
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "AGENTEVAL_A2A_I_UNDERSTAND_LIVE_SIDE_EFFECTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "   Set AGENTEVAL_A2A_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true to allow the live remote call.");
            return;
        }

        var judge = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        Console.WriteLine("① Calibrating both inter-agent boundary judges for this deployment…");
        var inbound = await InterAgentBoundaryInjectionGate.CalibrateInboundAsync(judge);
        var outbound = await InterAgentBoundaryInjectionGate.CalibrateOutboundAsync(judge);
        Console.WriteLine(
            $"   inbound  → accuracy {inbound.DecisiveAccuracy:P0}, " +
            $"missed {inbound.DangerousErrorCount}, inline-ready {inbound.IsInlineReady}");
        Console.WriteLine(
            $"   outbound → accuracy {outbound.DecisiveAccuracy:P0}, " +
            $"missed {outbound.DangerousErrorCount}, inline-ready {outbound.IsInlineReady}");

        if (!inbound.IsInlineReady || !outbound.IsInlineReady)
        {
            Console.WriteLine(
                "   STOP: at least one judge is not inline-ready; the remote agent was not resolved or called.");
            return;
        }

        Console.WriteLine("\n② Resolving the remote agent card and applying both boundary gates…");
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var resolver = new A2ACardResolver(
            remoteBaseUri,
            httpClient,
            "/.well-known/agent-card.json",
            logger: null);
        var remoteAgent = await resolver.GetAIAgentAsync(
            httpClient,
            options: null,
            loggerFactory: null,
            cancellationToken: default);

        var gatedRemoteAgent = remoteAgent
            .AsBuilder()
            .UseGatekeeper(
                AgentEval.MAF.Gatekeeper.GatekeeperEnforcement.ReplaceResult,
                options =>
                {
                    options.AddPreGate(
                        InterAgentBoundaryInjectionGate.CreateOutbound(
                            judge,
                            TrustedParentGoal));
                    options.AddPostGate(
                        InterAgentBoundaryInjectionGate.CreateInbound(judge));
                })
            .Build();

        Console.WriteLine("③ Sending one bounded, on-goal delegation through the guarded A2A proxy…");
        var response = await gatedRemoteAgent.RunAsync(DelegatedInstruction);
        Console.WriteLine($"   Remote response:\n   {response.Text}");
        Console.WriteLine("\n=== Gatekeeper — Real A2A Boundary Complete ===");
    }

    private static bool TryGetRemoteBaseUri(out Uri remoteBaseUri)
    {
        var configured = Environment.GetEnvironmentVariable("AGENTEVAL_A2A_BASE_URL");
        if (Uri.TryCreate(configured, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            string.IsNullOrEmpty(parsed.UserInfo))
        {
            remoteBaseUri = parsed;
            return true;
        }

        remoteBaseUri = null!;
        return false;
    }
}
