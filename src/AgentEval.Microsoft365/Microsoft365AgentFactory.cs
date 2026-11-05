// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Microsoft365;

/// <summary>
/// Builds the system-under-test behind <c>agenteval eval|bench --sut microsoft365</c> and the in-code
/// <c>IEvaluableAgent</c>: a Microsoft 365 agent reached over Work IQ A2A 1.0. Mirrors
/// <c>CopilotStudioAgentFactory</c>: <see cref="FromConversation"/> is the one seam the live path and the
/// credential-free tests share; <see cref="BuildLive"/> constructs the real transport
/// (<c>HttpClient(WorkIqAuthHandler → RawCaptureHandler → inner)</c>, A2A client, trust policy, conversation) and
/// hands it to that seam. <see cref="BuildLive"/> makes <b>no network call</b>; the token provider and card discovery
/// run lazily on the first turn.
/// </summary>
public static class Microsoft365AgentFactory
{
    /// <summary>Logical name of the one named <c>HttpClient</c> the live connector uses.</summary>
    public const string HttpClientName = "microsoft365";

    /// <summary>
    /// The LIVE path: validates consent and config, builds the authenticated A2A transport and a
    /// <see cref="WorkIqConversation"/>, and wraps it via <see cref="FromConversation"/>.
    /// </summary>
    /// <param name="config">The target's connection settings (validated here).</param>
    /// <param name="iUnderstandLiveSideEffects">
    /// Required, explicit consent that the returned agent drives a LIVE Microsoft 365 agent acting as the
    /// signed-in user — the delegated permission includes reads <b>and writes</b> to that user's Microsoft 365
    /// data, and a prompt can trigger real actions that cannot be sandboxed. Pass <see langword="true"/> only after
    /// confirming <paramref name="config"/> points at a non-production tenant/identity. There is deliberately no
    /// default: this method is directly callable from code, so the same acknowledgment is required of every caller,
    /// not just the CLI (which enforces its own <c>--i-understand-live-side-effects</c> flag first).
    /// </param>
    /// <param name="options">Runtime options; null means defaults.</param>
    /// <param name="tokenProvider">The token source; null uses <see cref="WorkIqDeviceCodeTokenProvider"/> (MSAL public client, silent → device code).</param>
    /// <param name="onTurn">Optional sink invoked with every turn result before the agent returns or throws.</param>
    /// <param name="innerHandler">Optional innermost <see cref="HttpMessageHandler"/> (tests pass a fake that asserts zero requests); null uses a <see cref="SocketsHttpHandler"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="iUnderstandLiveSideEffects"/> is <see langword="false"/>, or the config/options are invalid.</exception>
    public static WorkIqEvaluableAgent BuildLive(
        Microsoft365Config config,
        bool iUnderstandLiveSideEffects,
        WorkIqOptions? options = null,
        IWorkIqTokenProvider? tokenProvider = null,
        Action<WorkIqTurnResult>? onTurn = null,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!iUnderstandLiveSideEffects)
        {
            throw new InvalidOperationException(
                "BuildLive drives a LIVE Microsoft 365 agent over Work IQ as the signed-in user: the delegated " +
                "WorkIQAgent.Ask permission covers reads AND writes of that user's Microsoft 365 data, and a prompt can " +
                "trigger REAL actions that cannot be sandboxed. Pass iUnderstandLiveSideEffects: true only after " +
                "confirming config points at a NON-PROD tenant and identity (nothing was sent).");
        }

        config.Validate();
        (options ?? new WorkIqOptions()).Validate();

        throw new NotImplementedException("TODO(M24)");
    }

    /// <summary>
    /// Wraps any <see cref="IWorkIqConversation"/> as a <see cref="WorkIqEvaluableAgent"/>. The single seam the live
    /// path and the credential-free tests share: tests inject a fake conversation so the whole
    /// <c>--sut microsoft365</c> path (arg parsing → validation → consent → run → reporting) runs offline.
    /// Test-only as a target: the CLI never substitutes it for a live agent outside tests.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="name">The report-facing name.</param>
    /// <param name="options">Runtime options; null means defaults.</param>
    /// <param name="onTurn">Optional sink invoked with every turn result before the agent returns or throws.</param>
    public static WorkIqEvaluableAgent FromConversation(
        IWorkIqConversation conversation,
        string name,
        WorkIqOptions? options = null,
        Action<WorkIqTurnResult>? onTurn = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new WorkIqEvaluableAgent(conversation, name, options, onTurn);
    }
}
