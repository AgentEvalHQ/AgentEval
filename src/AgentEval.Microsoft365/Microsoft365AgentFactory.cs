// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

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

    /// <summary>How long a pooled connection of the default <see cref="SocketsHttpHandler"/> lives (DNS changes are picked up after it).</summary>
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

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
    /// <param name="tokenProvider">
    /// The token source; null uses <see cref="WorkIqDeviceCodeTokenProvider"/> (MSAL public client, silent → device code,
    /// its prompt on <see cref="Console.Error"/>). A host passes its own <see cref="WorkIqDeviceCodeTokenProvider"/> to
    /// choose the prompt writer or to share one sign-in with the readiness check; unless the host set its
    /// <see cref="WorkIqDeviceCodeTokenProvider.Diagnostics"/>, this agent's diagnostics are attached to it. A device-code
    /// sign-in that starts inside a turn (blocking, streamed or a subscription) is bounded by the code's expiry, not by
    /// <see cref="WorkIqOptions.TurnDeadline"/>, which restarts when the sign-in ends.
    /// </param>
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
        options ??= new WorkIqOptions();
        options.Validate();

        var clock = TimeProvider.System;
        var diagnostics = WorkIqDiagnosticsFactory.Create(config, options, clock);
        try
        {
            tokenProvider ??= new WorkIqDeviceCodeTokenProvider(config.TenantId, config.AppClientId, config.Scope) { Diagnostics = diagnostics };
            if (tokenProvider is WorkIqDeviceCodeTokenProvider deviceCode)
            {
                // A host-built device-code provider (its own prompt writer, perhaps shared with the readiness check) traces
                // this agent's sign-ins into this agent's run, unless the host gave it diagnostics of its own.
                deviceCode.AttachDiagnostics(diagnostics);
            }

            var (http, client) = CreateTransport(config, options, tokenProvider, diagnostics, clock, innerHandler);
            var conversation = new WorkIqConversation(client, config, options, clock, diagnostics);
            var agent = new WorkIqEvaluableAgent(conversation, config.DisplayName, options, onTurn, diagnostics);
            agent.Owned.Add(new SyncDisposableAdapter(http));
            agent.Owned.Add(new SyncDisposableAdapter(diagnostics));

            var cardUri = config.ResolveCardUri();
            diagnostics.Trace(
                WorkIqTraceStage.Config,
                "config.agent_built",
                $"live agent built for {config.DisplayName}: card {cardUri.Scheme}://{cardUri.Host}{cardUri.AbsolutePath}, agent id {(string.IsNullOrWhiteSpace(config.AgentId) ? "default" : "set")}, " +
                $"profile {client.Profile}, policy {options.ConversationPolicy}, MaxTurns {(options.MaxTurns == 0 ? "uncapped" : options.MaxTurns.ToString())}, " +
                $"token provider {tokenProvider.GetType().Name}, trusted hosts {string.Join(", ", config.TrustedHosts)}; no network call was made (discovery and sign-in run on the first turn)");
            return agent;
        }
        catch
        {
            diagnostics.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the authenticated transport of a live connection — <c>HttpClient(WorkIqAuthHandler → RawCaptureHandler →
    /// inner)</c> with the trust policy from <see cref="Microsoft365Config.TrustedHosts"/> and the HTTP timeout from
    /// <see cref="WorkIqOptions.SendTimeout"/> — and the A2A client over it. Shared by <see cref="BuildLive"/> and the
    /// readiness check (<c>agenteval m365 doctor</c>) so both reach Work IQ through exactly the same handler chain.
    /// Makes no network call. The returned <see cref="HttpClient"/> owns (and disposes) the handler chain, including
    /// <paramref name="innerHandler"/>.
    /// </summary>
    /// <param name="config">The validated config.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="tokenProvider">The token source the auth handler calls on every request.</param>
    /// <param name="diagnostics">The diagnostics every handler and the client trace through.</param>
    /// <param name="clock">The clock of the client.</param>
    /// <param name="innerHandler">The innermost handler; null uses a <see cref="SocketsHttpHandler"/>.</param>
    internal static (HttpClient Http, WorkIqA2AClient Client) CreateTransport(
        Microsoft365Config config,
        WorkIqOptions options,
        IWorkIqTokenProvider tokenProvider,
        WorkIqDiagnostics diagnostics,
        TimeProvider clock,
        HttpMessageHandler? innerHandler)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(clock);

        var policy = new WorkIqTrustPolicy(config.TrustedHosts);
        var inner = innerHandler ?? new SocketsHttpHandler { PooledConnectionLifetime = PooledConnectionLifetime };
        var capture = new RawCaptureHandler { InnerHandler = inner, Diagnostics = diagnostics };
        var auth = new WorkIqAuthHandler(tokenProvider)
        {
            InnerHandler = capture,
            Diagnostics = diagnostics,
            Policy = policy,
            ExpectedScope = config.Scope,
        };
        var http = new HttpClient(auth, disposeHandler: true)
        {
            // Zero means "no cap", like the other zero-valued options; HttpClient needs the explicit infinite value.
            Timeout = options.SendTimeout == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : options.SendTimeout,
        };

        // The client acquires the token before every call (Auth set), outside HttpClient.Timeout: a device-code sign-in is
        // bounded by its own code's expiry and the operation's token, and a sign-in that does not finish is reported as
        // "sign-in not completed, nothing was sent", never as a network timeout that may have reached the service. The
        // client selects the card's interface with the same trust policy the auth handler enforces.
        var client = new WorkIqA2AClient(config, options, http, capture, clock, diagnostics, policy) { Auth = auth };
        return (http, client);
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

    /// <summary>Lets a synchronous disposable (the HttpClient, the diagnostics) ride the agent's owned list.</summary>
    private sealed class SyncDisposableAdapter : IAsyncDisposable
    {
        private readonly IDisposable _inner;

        public SyncDisposableAdapter(IDisposable inner)
        {
            _inner = inner;
        }

        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
