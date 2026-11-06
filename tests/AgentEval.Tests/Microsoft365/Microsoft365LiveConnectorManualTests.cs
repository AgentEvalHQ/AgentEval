// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Microsoft365;

/// <summary>
/// <b>Manual, credentialed, gated tests — SKIPPED by default and never run in CI.</b> Everything else under
/// <c>tests/AgentEval.Tests/Microsoft365/</c> (and the CLI suite under <c>tests/AgentEval.Tests/Cli/Microsoft365/</c>)
/// is credential-free: fakes, recorded fixtures, or proofs that <c>BuildLive</c> makes no network call. These four
/// tests are the only ones that drive the live Work IQ A2A endpoint end to end, which nothing offline can substitute
/// for.
/// </summary>
/// <remarks>
/// <para><b>Setup (a non-production tenant and identity only):</b></para>
/// <list type="number">
/// <item><b>Work IQ enabled</b> in the tenant by an administrator, and a <b>Microsoft 365 Copilot licence</b> for the test
/// user. Without both the service answers 403 and the provider reports <c>Forbidden</c>.</item>
/// <item>An <b>Entra app registration configured as a public client</b>: on its <i>Authentication</i> blade, <i>Allow
/// public client flows</i> = <b>Yes</b> (otherwise sign-in fails with <c>AADSTS7000218</c>). There is no client
/// secret anywhere.</item>
/// <item>The <b>delegated permission <c>WorkIQAgent.Ask</c></b> (<c>api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask</c>)
/// on that app, with <b>admin consent</b> granted (otherwise sign-in fails with <c>AADSTS65001</c>). It covers reads
/// <b>and writes</b> of the signed-in user's Microsoft 365 data; the prompts here are read-only, but no sandbox exists.</item>
/// <item>Set <c>AGENTEVAL_M365_LIVE_TENANT_ID</c> and <c>AGENTEVAL_M365_LIVE_APP_CLIENT_ID</c>; optionally
/// <c>AGENTEVAL_M365_LIVE_AGENT_ID</c> (empty = the default Work IQ agent), <c>AGENTEVAL_M365_LIVE_ENDPOINT</c>
/// (default <c>https://workiq.svc.cloud.microsoft/a2a/</c>; its host must stay <c>workiq.svc.cloud.microsoft</c>, the
/// only trusted host here) and <c>AGENTEVAL_M365_LIVE_AGENT_NAME</c> (a display name). No other variable is read; do
/// not unset these when you clear the paid-provider variables.</item>
/// <item>In a local, uncommitted edit remove <c>Skip = SkipReason</c> from the test(s) to run, then run them alone:
/// <c>dotnet test tests/AgentEval.Tests --framework net10.0 --filter "FullyQualifiedName~Microsoft365LiveConnectorManualTests" --logger "console;verbosity=detailed"</c>
/// (the detailed logger shows what each test prints: the card census, the outcome, ids, the evidence availability).</item>
/// </list>
/// <para>
/// <b>The device-code prompt appears on the first run</b> for a (tenant, app) pair: MSAL writes "To sign in, use a web
/// browser to open … and enter the code …" to stderr and waits (about 15 minutes). <c>dotnet test</c> may not stream
/// stderr while a test runs; the same message is the <c>auth.device_code_prompted</c> event of the trace file each test
/// prints (readable while the run waits, e.g. <c>Get-Content -Wait</c>). The simplest path is to sign in once with
/// sample R1 (<c>samples/AgentEval.Samples</c>, Microsoft 365 Hello World) or <c>agenteval m365 doctor</c> on the same
/// tenant and app: the OS-encrypted token cache is keyed by tenant and app, so these tests then run silently.
/// </para>
/// <para>
/// Each test writes its trace and issue register under <c>&lt;temp&gt;/agenteval-m365-live/</c> and prints both paths;
/// on a failure, the issue register names the stage, the HTTP / JSON-RPC / AADSTS codes and a suggested cause. Live
/// sends: test 1 none (one card GET, one <c>GetTask</c>), test 2 one turn, test 3 three turns, test 4 one turn and one
/// <c>GetTask</c>.
/// </para>
/// <para>
/// <b>What a green run proves</b> — the pieces that cannot be verified offline: device-code sign-in returns a token
/// the Work IQ endpoint accepts, and the persisted cache makes a second run silent; the agent card is served, parses
/// as A2A 1.0 and advertises a JSON-RPC 1.0 interface on a trusted host (test 1); the endpoint answers a JSON-RPC
/// request with an A2A 1.0 <c>TaskNotFound</c> error, so the protocol handshake works end to end (test 1); a real
/// read-only turn ends <c>Completed</c> with a non-empty answer and an evidence document whose tool channel is
/// <c>NotRequested</c> under the default profile (test 2); the service keeps a context the client continues and
/// starts a new one after a local reset (test 3); and <c>GetTask</c> on the handle of a completed turn returns that
/// task, completed (test 4). It does not prove anything about answer quality, citations, or what the agent did in the
/// tenant: a reply is evidence of what was SAID.
/// </para>
/// <para>
/// Never publish what these tests print or write (answers, ids, trace files) without clearing it first: see the live
/// acceptance runbook. A capture is never presented as a live run.
/// </para>
/// </remarks>
public sealed class Microsoft365LiveConnectorManualTests(ITestOutputHelper output)
{
    private const string SkipReason =
        "Manual/live-credentialed test — requires a non-prod Microsoft 365 tenant with Work IQ enabled, a Microsoft 365 " +
        "Copilot licence for the test user, and a public-client Entra app with the delegated WorkIQAgent.Ask permission " +
        "(admin consent). Never runs in CI. See this class's XML doc remarks for setup, then remove this Skip to run locally.";

    private const string ReadOnlyPrompt = "In one sentence, what can you help me with? Cite nothing.";
    private const string FollowUpPrompt = "Repeat your previous answer in at most ten words.";

    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Discovery and the protocol handshake, with no turn sent: the card is fetched and a JSON-RPC 1.0 interface on a
    /// trusted host is selected (the card census is printed: interface, digest, streaming, modes, skills); then
    /// <c>GetTask</c> on a random id must come back as the A2A <c>TaskNotFound</c> error.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task GetTaskAsync_RandomTaskIdAfterDiscovery_ComesBackTaskNotFound()
    {
        var config = LoadConfig();
        var options = NewOptions(maxTurns: 1);
        await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options: options);
        var paths = PrintPaths(options);
        var conversation = Assert.IsType<WorkIqConversation>(agent.Conversation);

        // Discovery: the card GET with the trust policy, then the JSON-RPC 1.0 interface selection.
        var card = await conversation.Client.DiscoverAsync(CancellationToken.None);
        _output.WriteLine($"Card:        {card.Card.Name} (version {card.Card.Version}) from {card.CardUri}");
        _output.WriteLine($"Interface:   {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath} (A2A {card.SelectedProtocolVersion})");
        _output.WriteLine($"Card sha256: {card.Sha256}");
        _output.WriteLine($"Streaming:   {Capability(card.Card.Capabilities?.Streaming)} · push notifications: {Capability(card.Card.Capabilities?.PushNotifications)}");
        _output.WriteLine($"Input modes: {Modes(card.Card.DefaultInputModes)} · output modes: {Modes(card.Card.DefaultOutputModes)}");
        _output.WriteLine($"Skills:      {(card.Card.Skills is { Count: > 0 } skills ? string.Join(", ", skills.Select(s => s.Name)) : "(none advertised)")}");
        Assert.Equal(Microsoft365Evidence.A2AVersion, card.SelectedProtocolVersion);

        // Handshake: GetTask on an id nobody created. The endpoint must answer as A2A 1.0 JSON-RPC — TaskNotFound.
        var routingTenant = (conversation.Client as WorkIqA2AClient)?.RoutingTenant;
        var handle = new WorkIqTaskHandle(
            card.SelectedEndpoint,
            card.SelectedProtocolVersion,
            config.AgentId,
            Guid.NewGuid().ToString("D"),
            ContextId: null,
            routingTenant,
            conversation.PrincipalKey);
        var refused = await Assert.ThrowsAsync<WorkIqProtocolException>(() => conversation.GetTaskAsync(handle));
        _output.WriteLine($"GetTask(random id): {refused.Message}");
        _output.WriteLine($"Issues registered so far: {agent.Diagnostics.IssueCount} (the not-found reply is one) → {paths.IssueFile.FullName}");

        Assert.Equal(WorkIqFailureCategory.NotFound, refused.Failure.Category);
        // An HTTP 404 without a JSON-RPC error would mean the endpoint URL itself was not found (check endpoint and
        // agentId), not that the task was: only the A2A TaskNotFound code proves the handshake.
        Assert.Equal((int)global::A2A.A2AErrorCode.TaskNotFound, refused.Failure.RpcErrorCode);
    }

    /// <summary>
    /// One read-only turn ends <c>Completed</c> with a non-empty answer; prints the outcome dimensions, task and context
    /// ids, timings, the evidence availability per channel, the captures and the trace path.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task InvokeAsync_ReadOnlyPrompt_CompletesWithEvidence()
    {
        var config = LoadConfig();
        var options = NewOptions(maxTurns: 1);
        var turns = new List<WorkIqTurnResult>();
        await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options: options, onTurn: turns.Add);
        var paths = PrintPaths(options);

        var reply = await InvokeCompletedAsync(agent, ReadOnlyPrompt, paths);
        var turn = Assert.Single(turns);
        PrintTurn(turn);
        _output.WriteLine($"Answer:      {Trunc(reply.Text)}");

        var evidence = reply.Evidence();
        Assert.NotNull(evidence);
        PrintEvidence(evidence);

        Assert.Equal(WorkIqTurnOutcome.Completed, turn.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text), "a completed turn carried a blank answer");
        // LoadConfig leaves diagnosticProfile unset, so the profile is PublicAnswerOnly: no tool channel is requested,
        // and none may be reported as anything but NotRequested.
        Assert.Equal(DiagnosticProfile.PublicAnswerOnly, config.ResolveProfile());
        Assert.Equal(EvidenceAvailability.NotRequested, evidence.ToolExecutions.Availability);
    }

    /// <summary>
    /// Under <see cref="ConversationPolicy.ContinueContext"/> a follow-up turn comes back in the first turn's context,
    /// and after <c>ResetSessionAsync</c> the next turn gets a new one (three turns).
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task InvokeAsync_ContinueThenReset_KeepsThenReplacesTheContextId()
    {
        var config = LoadConfig();
        var options = NewOptions(maxTurns: 3) with { ConversationPolicy = ConversationPolicy.ContinueContext };
        var turns = new List<WorkIqTurnResult>();
        await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options: options, onTurn: turns.Add);
        var paths = PrintPaths(options);

        await InvokeCompletedAsync(agent, ReadOnlyPrompt, paths);
        var first = turns[^1];
        await InvokeCompletedAsync(agent, FollowUpPrompt, paths);
        var continued = turns[^1];
        await agent.ResetSessionAsync();
        await InvokeCompletedAsync(agent, ReadOnlyPrompt, paths);
        var fresh = turns[^1];

        _output.WriteLine($"first turn:                {first.ContextId ?? "none"} (task {first.Task?.TaskId ?? "none"})");
        _output.WriteLine($"continued (same agent):    {continued.ContextId ?? "none"} (task {continued.Task?.TaskId ?? "none"})");
        _output.WriteLine($"after ResetSessionAsync(): {fresh.ContextId ?? "none"} (task {fresh.Task?.TaskId ?? "none"})");

        if (first.ContextId is null)
        {
            Assert.Fail("NOT MEASURED: the service returned no context id on the first turn, so continuity cannot be compared.");
            return;
        }

        Assert.Equal(first.ContextId, continued.ContextId);
        Assert.NotNull(fresh.ContextId);
        Assert.NotEqual(first.ContextId, fresh.ContextId);
    }

    /// <summary>
    /// <c>GetTask</c> on the handle of a completed turn returns that task in <c>TASK_STATE_COMPLETED</c>, without
    /// resending anything. A direct-message reply (no task) is reported as NOT MEASURED, never as a pass.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task GetTaskAsync_CompletedTurnHandle_ReturnsTheCompletedTask()
    {
        var config = LoadConfig();
        var options = NewOptions(maxTurns: 1);
        var turns = new List<WorkIqTurnResult>();
        await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options: options, onTurn: turns.Add);
        var paths = PrintPaths(options);

        await InvokeCompletedAsync(agent, ReadOnlyPrompt, paths);
        var turn = Assert.Single(turns);
        PrintTurn(turn);
        if (turn.Task is not { } task)
        {
            Assert.Fail("NOT MEASURED: the service answered with a direct message (no task), so there is no handle to read back.");
            return;
        }

        var snapshot = await agent.Conversation.GetTaskAsync(task);
        _output.WriteLine($"GetTask:     state {snapshot.RawState ?? "(absent)"} · {(snapshot.Artifacts.IsDefault ? 0 : snapshot.Artifacts.Length)} artifact(s) · " +
                          $"{(snapshot.History.IsDefault ? 0 : snapshot.History.Length)} history message(s) · context {snapshot.Handle.ContextId ?? "none"}");

        Assert.Equal(task.TaskId, snapshot.Handle.TaskId);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
    }

    private static Microsoft365Config LoadConfig()
    {
        var config = new Microsoft365Config
        {
            TenantId = RequireEnv("AGENTEVAL_M365_LIVE_TENANT_ID"),
            AppClientId = RequireEnv("AGENTEVAL_M365_LIVE_APP_CLIENT_ID"),
            AgentId = OptionalEnv("AGENTEVAL_M365_LIVE_AGENT_ID"),
            AgentName = OptionalEnv("AGENTEVAL_M365_LIVE_AGENT_NAME"),
        };
        if (OptionalEnv("AGENTEVAL_M365_LIVE_ENDPOINT") is { } endpoint)
            config = config with { Endpoint = endpoint };

        config.Validate();
        return config;
    }

    // Every test writes its own run's trace under one temp folder (the issue register is shared, one line per issue
    // with its run id); the default profile, content policy and timeouts are the ones a user gets.
    private static WorkIqOptions NewOptions(int maxTurns) => new()
    {
        MaxTurns = maxTurns,
        RunId = Microsoft365Diagnostics.NewRunId(),
        DiagnosticsDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-m365-live")),
    };

    private Microsoft365DiagnosticsPaths PrintPaths(WorkIqOptions options)
    {
        var paths = Microsoft365Diagnostics.Describe(options);
        _output.WriteLine($"Trace:       {paths.TraceFile.FullName}");
        _output.WriteLine($"Issues:      {paths.IssueFile.FullName}");
        return paths;
    }

    // A non-completed turn fails the test with what a tester needs: outcome, ids, the safe failure summary and where the
    // diagnostics are. Its partial text is never treated as an answer.
    private static async Task<AgentResponse> InvokeCompletedAsync(WorkIqEvaluableAgent agent, string prompt, Microsoft365DiagnosticsPaths paths)
    {
        try
        {
            return await agent.InvokeAsync(prompt);
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            var result = ex.TurnResult;
            var failure = result.Failure is { } f ? $"{f.Category} ({f.Certainty}) during {f.Operation}: {f.SafeMessage}" : "none";
            Assert.Fail(
                $"The turn did not complete: outcome {result.Outcome}, task {result.Task?.TaskId ?? "none"}, " +
                $"context {result.ContextId ?? "none"}, transport {result.Transport}, failure {failure}. " +
                $"Trace: {paths.TraceFile.FullName}; issues: {paths.IssueFile.FullName}.");
            throw;
        }
    }

    private void PrintTurn(WorkIqTurnResult turn)
    {
        var timing = turn.Timing;
        var firstByte = timing.FirstEventAt is { } at ? $"{(at - timing.StartedAt).TotalMilliseconds:F0} ms" : "not observed";
        _output.WriteLine($"Outcome:     {turn.Outcome} · transport {turn.Transport} · answer {turn.AnswerCompleteness} · telemetry {turn.Telemetry}");
        _output.WriteLine($"Ids:         task {turn.Task?.TaskId ?? "none (direct message)"} · context {turn.ContextId ?? "none"} · rpc {turn.Identity.RpcRequestId}");
        _output.WriteLine($"Timing:      first event {firstByte} · total {timing.Elapsed.TotalMilliseconds:F0} ms · GetTask/Subscribe recovery requests {timing.RecoveryRequests}");
    }

    private void PrintEvidence(Microsoft365TurnEvidence evidence)
    {
        _output.WriteLine($"Task state:  {evidence.TaskState ?? "(no task)"} · capture {evidence.CaptureState}");
        _output.WriteLine($"  citation markers     {Availability(evidence.CitationMarkers)}");
        _output.WriteLine($"  citation sources     {Availability(evidence.CitationSources)}");
        _output.WriteLine($"  tool selections      {Availability(evidence.ToolSelections)}");
        _output.WriteLine($"  tool executions      {Availability(evidence.ToolExecutions)}");
        _output.WriteLine($"  retrieval executions {Availability(evidence.RetrievalExecutions)}");
        _output.WriteLine($"  approval intents     {Availability(evidence.ApprovalIntents)}");
        _output.WriteLine($"  skill activations    {Availability(evidence.SkillActivations)}");
        if (evidence.CitationMarkers.IsProvided && !evidence.CitationMarkers.Items.IsDefaultOrEmpty)
            _output.WriteLine($"Marker formats: {string.Join(", ", evidence.CitationMarkers.Items.Select(m => m.MarkerFormat).Distinct(StringComparer.Ordinal))}");
        if (!evidence.CitationLinks.IsDefaultOrEmpty)
            _output.WriteLine($"Citation links: {string.Join(", ", evidence.CitationLinks.GroupBy(l => l.Binding).Select(g => $"{g.Count()} {g.Key}"))}");
        if (!evidence.RawCaptures.IsDefault)
        {
            foreach (var capture in evidence.RawCaptures)
                _output.WriteLine($"Capture:     {capture.CaptureId} · {capture.State} · {capture.ByteCount?.ToString() ?? "no"} bytes · sha256 {capture.Sha256 ?? "none"}");
        }
        _output.WriteLine($"Normalisation issues: {(evidence.Issues.IsDefault ? 0 : evidence.Issues.Length)}");
    }

    private static string Availability<T>(EvidenceSet<T> set) =>
        set.IsProvided
            ? $"Provided ({(set.Items.IsDefault ? 0 : set.Items.Length)} item(s), {set.Validation}, coverage {set.Coverage}, scope {set.Scope})"
            : set.Availability.ToString();

    private static string Capability(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "not declared",
    };

    private static string Modes(List<string>? modes) => modes is { Count: > 0 } ? string.Join(", ", modes) : "(none)";

    private static string Trunc(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 120 ? s : s[..120] + "…";

    private static string RequireEnv(string name) =>
        OptionalEnv(name) ?? throw new InvalidOperationException($"Set {name} to run this manual live test.");

    private static string? OptionalEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
