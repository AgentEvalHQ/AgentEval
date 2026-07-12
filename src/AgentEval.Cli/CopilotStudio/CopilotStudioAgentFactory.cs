// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using Microsoft.Agents.AI;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// Builds the system-under-test behind <c>redteam --sut copilot-studio</c>: a Microsoft Copilot Studio (MCS) agent
/// fronted as an <see cref="IEvaluableAgent"/>. Mirrors <c>AzureChatAgentFactory</c> and <c>GatekeeperDemoSut</c>
/// — a MAF <see cref="AIAgent"/> wrapped in a <see cref="MAFAgentAdapter"/> (the same proven seam the Foundry
/// integration uses), rather than a bespoke <c>IChatClient</c>.
/// </summary>
/// <remarks>
/// <para><b>Fidelity ceiling.</b> The conversation channel makes server-side tool calls structurally invisible, so an
/// MCS scan runs at <c>SutTier.TextOnly</c> / <c>EvidenceFidelity.Verbal</c> — missing tool evidence resolves to
/// Inconclusive, never a fabricated PASS. Behavioral fidelity requires the deferred L2 telemetry-enrichment path.</para>
/// <para><b>Live connector deferred.</b> <see cref="FromAgent"/> — the seam both the live path and the CI tests go
/// through — is complete. <see cref="BuildLive"/> (constructing the connector-backed agent from config + auth) is
/// wired once the Copilot Studio connector package + API are verified against the current MAF release with a real
/// non-prod agent; until then it throws a clear, actionable error.</para>
/// </remarks>
internal static class CopilotStudioAgentFactory
{
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
    /// <b>Deferred</b> — throws until the connector package/API are verified against the current MAF release. The
    /// scaffold, safety gates, and CI path are complete without it.
    /// </summary>
    public static IEvaluableAgent BuildLive(CopilotStudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();   // fail on bad config before the "not wired" message, so the error the caller sees is theirs

        throw new NotSupportedException(
            "The live Copilot Studio connector is not wired yet, so --sut copilot-studio cannot run a live scan in " +
            "this release. The scaffold, safety gates, and credential-free CI path are complete; wiring the connector " +
            "(the Microsoft.Agents.CopilotStudio.Client package + device-code auth, verified against the current MAF " +
            "release) is still to come. For now, red-team an OpenAI-compatible or Azure endpoint with --endpoint / " +
            "--azure, or try the credential-free --sut gatekeeper-demo.");
    }
}
