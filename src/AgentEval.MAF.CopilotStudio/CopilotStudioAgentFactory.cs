// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using CopilotClient = Microsoft.Agents.CopilotStudio.Client.CopilotClient;
using ConnectionSettings = Microsoft.Agents.CopilotStudio.Client.ConnectionSettings;

namespace AgentEval.MAF.CopilotStudio;

/// <summary>
/// Builds the system-under-test behind <c>redteam --sut copilot-studio</c>: a Microsoft Copilot Studio (MCS) agent
/// fronted as an <see cref="IEvaluableAgent"/>. Mirrors <c>AzureChatAgentFactory</c> and <c>GatekeeperDemoSut</c>
/// — a MAF <see cref="AIAgent"/> wrapped in a <see cref="MAFAgentAdapter"/> (the same proven seam the Foundry
/// integration uses).
/// </summary>
/// <remarks>
/// <para><b>Fidelity ceiling.</b> The conversation channel makes server-side tool calls structurally invisible, so an
/// MCS scan runs at <c>SutTier.TextOnly</c> / <c>EvidenceFidelity.Verbal</c> — missing tool evidence resolves to
/// Inconclusive, never a fabricated PASS. Behavioral fidelity requires the deferred L2 telemetry-enrichment path.</para>
/// <para><b>Live connector.</b> <see cref="FromAgent"/> — the seam both the live path and the CI tests go through —
/// wraps whatever <see cref="AIAgent"/> it's given. <see cref="BuildLive"/> now constructs a real
/// <c>Microsoft.Agents.CopilotStudio.Client.CopilotClient</c> from a <see cref="CopilotStudioConfig"/> (via
/// <see cref="ConnectionSettings"/> + MSAL device-code auth), bridges its streaming activity API into an
/// <see cref="IChatClient"/> (<see cref="CopilotStudioChatClient"/>), wraps that in a MAF <c>ChatClientAgent</c>,
/// and hands it to <see cref="FromAgent"/> — exactly the same seam the credential-free tests already exercise.
/// See <see cref="CopilotStudioChatClient"/> and <see cref="CopilotStudioTokenProvider"/> for what is and is not
/// independently live-verified; a real Entra app registration + non-prod Copilot Studio agent is still required
/// for an end-to-end smoke test before this is trusted in production.</para>
/// </remarks>
public static class CopilotStudioAgentFactory
{
    /// <summary>Logical name of the one <see cref="IHttpClientFactory"/>-named client the live connector uses.</summary>
    internal const string HttpClientName = "copilotstudio";

    /// <summary>
    /// Wraps an already-constructed MAF <see cref="AIAgent"/> as an <see cref="IEvaluableAgent"/>. The single seam the
    /// live path and the CI tests share: tests inject a <c>FakeChatClient</c>-backed <c>ChatClientAgent</c> so the
    /// whole <c>--sut copilot-studio</c> path (arg parsing → validation → gates → scan → reporting) runs
    /// credential-free and offline.
    /// </summary>
    public static IEvaluableAgent FromAgent(AIAgent innerAgent)
    {
        ArgumentNullException.ThrowIfNull(innerAgent);
        return new MAFAgentAdapter(innerAgent);
    }

    /// <summary>
    /// The LIVE path: build a MAF <see cref="AIAgent"/> from the Copilot Studio connector using
    /// <paramref name="config"/> + device-code/MSAL auth, then wrap it via <see cref="FromAgent"/>.
    /// </summary>
    /// <remarks>
    /// Everything this method does is local/offline — constructing <see cref="ConnectionSettings"/>, resolving the
    /// token scope, and building the <see cref="CopilotClient"/> — makes <b>no network call</b>. The
    /// <see cref="CopilotStudioTokenProvider"/> callback it wires up is only invoked lazily, by
    /// <see cref="CopilotClient"/> itself, on the first real request the returned agent makes. This preserves the
    /// existing "consent gate runs before any network call" guarantee: <c>CopilotStudioRedTeamTarget.Validate</c>'s
    /// consent/config checks (and any equivalent gate in a future <c>eval</c>/<c>bench</c> caller) run and can
    /// refuse well before this method — let alone a network call — is ever reached.
    /// </remarks>
    public static IEvaluableAgent BuildLive(CopilotStudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        var settings = new ConnectionSettings
        {
            EnvironmentId = config.EnvironmentId,
            SchemaName = config.SchemaName,
            Cloud = config.ResolveCloud(),
        };

        // Never hardcode the scope — the SDK computes the correct audience for the resolved cloud/settings.
        var scope = CopilotClient.ScopeFromSettings(settings);
        var tokenProvider = new CopilotStudioTokenProvider(config.TenantId, config.AppClientId, scope);
        var httpClientFactory = new SingleNameHttpClientFactory(HttpClientName);

        var copilotClient = new CopilotClient(
            settings,
            httpClientFactory,
            tokenProvider.GetTokenAsync,
            NullLogger.Instance,
            HttpClientName);

        IChatClient chatClient = new CopilotStudioChatClient(new CopilotClientConversationAdapter(copilotClient));
        AIAgent innerAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = config.DisplayName });
        return FromAgent(innerAgent);
    }
}
