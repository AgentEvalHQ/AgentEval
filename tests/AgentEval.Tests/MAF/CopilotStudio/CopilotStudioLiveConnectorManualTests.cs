// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.CopilotStudio;
using Xunit;

namespace AgentEval.Tests.MAF.CopilotStudio;

/// <summary>
/// <b>Manual, credentialed, gated test — SKIPPED by default and never run in CI.</b> Everything else under
/// <c>tests/AgentEval.Tests/MAF/CopilotStudio/</c> (and the CLI-specific suite under
/// <c>tests/AgentEval.Tests/Cli/CopilotStudio/</c>) is credential-free (fakes, or proves <c>BuildLive</c>'s
/// construction path makes no network call). This is the one test in the suite that actually drives the live
/// Copilot Studio connector end to end — the thing genuine confidence in this feature still requires and that
/// nothing else in this repo can substitute for.
/// </summary>
/// <remarks>
/// <para><b>To run this locally once you have a real, non-prod Copilot Studio agent + Entra app registration:</b></para>
/// <list type="number">
/// <item>Publish a Copilot Studio agent in a **non-production** Power Platform environment (its connectors/flows
/// can fire real actions — never point this at anything production).</item>
/// <item>Register a public-client Entra app with the Power Platform API's <c>CopilotStudio.Copilots.Invoke</c>
/// delegated permission (see Microsoft's Direct-to-Engine / Copilot Studio Client docs for the exact steps —
/// this changes across MCS releases, so this comment intentionally doesn't restate a specific click-path).</item>
/// <item>Set the six <c>AGENTEVAL_CS_LIVE_*</c> environment variables named below.</item>
/// <item>Remove the <c>Skip</c> argument from the <c>[Fact]</c> attribute (or run with a test filter that
/// includes skipped tests) and run this test alone. The FIRST run will print a device-code URL + code to the
/// console (via <c>Console.Error</c>, from <see cref="CopilotStudioTokenProvider"/>) — complete that sign-in in a
/// browser. Subsequent runs reuse the persisted MSAL cache silently.</item>
/// </list>
/// <para>
/// <b>What a green run here actually proves</b> — the pieces that genuinely cannot be verified without this:
/// device-code sign-in completes and returns a usable token; the persisted cache round-trips (a second run
/// doesn't re-prompt); <c>ConnectionSettings</c> + the resolved scope reach a real Copilot Studio endpoint and get
/// a real HTTP response (not just a well-formed request); the activity stream a REAL agent emits parses the way
/// <c>CopilotStudioChatClient</c> assumes (in particular: does a real agent emit any non-"message" activity on
/// <c>StartConversationAsync</c> that should be surfaced, and does a real multi-activity turn look like the fakes
/// in <c>CopilotStudioChatClientTests</c> assume). None of this is guessable from the SDK's public surface alone.
/// </para>
/// </remarks>
public class CopilotStudioLiveConnectorManualTests
{
    private const string SkipReason =
        "Manual/live-credentialed test — requires a real Entra app registration + a non-prod Copilot Studio " +
        "agent. Never runs in CI. See this class's XML doc remarks for setup, then remove this Skip to run locally.";

    [Fact(Skip = SkipReason)]
    public async Task BuildLive_RealAgent_RespondsToASimpleQuestion()
    {
        var config = new CopilotStudioConfig
        {
            EnvironmentId = RequireEnv("AGENTEVAL_CS_LIVE_ENVIRONMENT_ID"),
            SchemaName = RequireEnv("AGENTEVAL_CS_LIVE_SCHEMA_NAME"),
            TenantId = RequireEnv("AGENTEVAL_CS_LIVE_TENANT_ID"),
            AppClientId = RequireEnv("AGENTEVAL_CS_LIVE_APP_CLIENT_ID"),
            Cloud = Environment.GetEnvironmentVariable("AGENTEVAL_CS_LIVE_CLOUD"), // optional, defaults to Prod
            AgentName = Environment.GetEnvironmentVariable("AGENTEVAL_CS_LIVE_AGENT_NAME"),
        };

        var agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true);
        var response = await agent.InvokeAsync("Hello — please reply with a short greeting.");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} to run this manual live test.");
}
