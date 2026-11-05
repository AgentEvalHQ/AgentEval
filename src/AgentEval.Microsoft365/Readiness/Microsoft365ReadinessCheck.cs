// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>
/// The readiness check an external tester runs first on their own tenant (<c>agenteval m365 doctor</c>): staged,
/// in order, each with its evidence and a plain-language verdict, so whoever reads the report knows exactly which
/// stage failed, why, and what to do next — without reading code.
/// </summary>
/// <remarks>
/// <para>
/// Stages: (1) <see cref="ReadinessStage.Config"/> loads and validates the config (no network); (2)
/// <see cref="ReadinessStage.Auth"/> acquires a token — silent first, device-code sign-in otherwise (the MSAL
/// sign-in message is the one auth text that is shown); (3) <see cref="ReadinessStage.Discovery"/> fetches the agent
/// card through the same handler chain a live agent uses and selects the A2A 1.0 JSON-RPC interface on a trusted
/// host; (4) <see cref="ReadinessStage.Protocol"/> sends <c>GetTask</c> for a random id (<see cref="ProtocolTaskIdPrefix"/>
/// + a GUID) and passes on the JSON-RPC TaskNotFound (-32001) answer; (5) <see cref="ReadinessStage.Probe"/>, opt-in
/// and only with consent, runs one turn with <see cref="ProbePrompt"/> in a fresh context and passes only on
/// <see cref="WorkIqTurnOutcome.Completed"/>; (6) <see cref="ReadinessStage.Census"/>, after a probe, records what the
/// tenant does with the experimental native118 profile (diagnostic modes accepted, Developer Info / Retrieval Info
/// artifacts present, citation marker format) and what <c>CancelTask</c> returns on a finished task.
/// </para>
/// <para>
/// The check stops after the first failure unless <see cref="ReadinessCheckOptions.ContinueAfterFailure"/>; a stage
/// that does not run is <see cref="ReadinessResult.Skipped"/> with its reason, never a pass. Every stage is traced on the
/// <c>Doctor</c> stage (<c>doctor.&lt;stage&gt;.start</c>, then <c>.pass</c> / <c>.fail</c> / <c>.skipped</c>) next to
/// the underlying seams' own events, and every failed stage is registered as a <c>doctor.&lt;stage&gt;.failed</c>
/// issue. The two replies the doctor provokes on purpose — TaskNotFound on the random id, TaskNotCancelable on the
/// finished task — are kept out of the issue register (the trace records them and says so). No output ever carries a
/// token, an auth code or the <c>Authorization</c> header; the trace never carries the prompt or the answer (the
/// report shows the probe's prompt and the first 80 characters of its answer).
/// </para>
/// </remarks>
public static class Microsoft365ReadinessCheck
{
    /// <summary>The one prompt the probe and the census send: benign and read-only.</summary>
    public const string ProbePrompt = "Reply with the single word READY.";

    /// <summary>The prefix of the random task id the protocol stage asks for.</summary>
    public const string ProtocolTaskIdPrefix = "doctor-";

    /// <summary>
    /// Runs the readiness check and returns the report. Runtime failures (bad config, sign-in errors, HTTP and
    /// JSON-RPC errors, timeouts) become failed stages, not exceptions; the trace and issue files are closed when the
    /// returned task completes.
    /// </summary>
    /// <param name="config">The config, when given in code; exactly one of this and <paramref name="configFile"/>.</param>
    /// <param name="configFile">The config file (the <c>--m365-config</c> JSON); exactly one of this and <paramref name="config"/>.</param>
    /// <param name="options">What to run and how.</param>
    /// <param name="cancellationToken">Stops the check: the running stage fails as canceled, the rest are skipped.</param>
    /// <exception cref="ArgumentException">Both or neither of <paramref name="config"/> and <paramref name="configFile"/> were given.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ReadinessCheckOptions.StageTimeout"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The trace or issue file could not be opened (the message names the path).</exception>
    public static Task<ReadinessReport> RunAsync(
        Microsoft365Config? config,
        FileInfo? configFile,
        ReadinessCheckOptions options,
        CancellationToken cancellationToken = default) =>
        RunAsync(config, configFile, options, innerHandler: null, tokenProvider: null, cancellationToken);

    /// <summary>
    /// The seam of <see cref="RunAsync(Microsoft365Config, FileInfo, ReadinessCheckOptions, CancellationToken)"/> with the
    /// innermost HTTP handler and the token provider injectable (offline tests).
    /// </summary>
    /// <param name="config">The config, when given in code.</param>
    /// <param name="configFile">The config file.</param>
    /// <param name="options">What to run and how.</param>
    /// <param name="innerHandler">The innermost HTTP handler; null uses a socket handler.</param>
    /// <param name="tokenProvider">The token source; null uses the MSAL device-code provider with <see cref="ReadinessCheckOptions.Console"/> as the prompt writer.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    internal static async Task<ReadinessReport> RunAsync(
        Microsoft365Config? config,
        FileInfo? configFile,
        ReadinessCheckOptions options,
        HttpMessageHandler? innerHandler,
        IWorkIqTokenProvider? tokenProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((config is null) == (configFile is null))
        {
            throw new ArgumentException("Pass exactly one of config and configFile.", config is null ? nameof(configFile) : nameof(config));
        }

        options.Validate();
        await using var run = new ReadinessRun(config, configFile, options, innerHandler, tokenProvider, TimeProvider.System);
        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }
}
