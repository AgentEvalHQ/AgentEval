// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Binds tasks to one conversation. It builds task handles that carry the conversation's endpoint, protocol version,
/// agent, routing tenant and principal key. It refuses a handle of another endpoint or principal. It creates the task
/// reducers, whose handle template carries the same facts.
/// </summary>
internal sealed class TaskHandleFactory
{
    private readonly Microsoft365Config _config;
    private readonly TimeProvider _clock;
    private readonly WorkIqDiagnostics _diagnostics;

    /// <summary>Creates the factory of a validated configuration.</summary>
    /// <param name="config">The validated connection settings.</param>
    /// <param name="clock">The clock the reducers use.</param>
    /// <param name="diagnostics">The diagnostics the reducers trace through.</param>
    public TaskHandleFactory(Microsoft365Config config, TimeProvider clock, WorkIqDiagnostics diagnostics)
    {
        _config = config;
        _clock = clock;
        _diagnostics = diagnostics;
        PrincipalKey = ComputePrincipalKey(config.TenantId, config.AppClientId, config.Scope);
    }

    /// <summary>
    /// A stable, non-secret key of the principal the conversation acts for: SHA-256 of <c>tenantId|appClientId|scope</c>.
    /// The signed-in account is not known at this layer, and no token is ever used. A handle from another tenant, app
    /// or scope is refused.
    /// </summary>
    public string PrincipalKey { get; }

    /// <summary>The handle of a task of this conversation.</summary>
    /// <param name="card">The discovered card.</param>
    /// <param name="taskId">The task id.</param>
    /// <param name="contextId">The context id, when any.</param>
    public WorkIqTaskHandle Build(RawAgentCard card, string taskId, string? contextId) =>
        new(
            card.SelectedEndpoint,
            card.SelectedProtocolVersion,
            string.IsNullOrWhiteSpace(_config.AgentId) ? null : _config.AgentId.Trim(),
            taskId,
            string.IsNullOrWhiteSpace(contextId) ? null : contextId,
            card.RoutingTenant,
            PrincipalKey);

    /// <summary>Throws <see cref="InvalidOperationException"/> when the handle belongs to another endpoint or principal.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="card">The discovered card.</param>
    public void Validate(WorkIqTaskHandle handle, RawAgentCard card)
    {
        if (handle.Endpoint != card.SelectedEndpoint)
        {
            throw new InvalidOperationException(
                $"The task handle belongs to another endpoint ({handle.Endpoint.Host}{handle.Endpoint.AbsolutePath}); this conversation talks to {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath}.");
        }

        if (!string.Equals(handle.PrincipalKey, PrincipalKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The task handle belongs to another principal (tenant, app or scope); it cannot be read through this conversation.");
        }
    }

    /// <summary>The reducer of a turn. It needs the discovered card.</summary>
    /// <param name="ctx">The turn.</param>
    public WorkIqTaskReducer NewReducer(TurnContext ctx) =>
        NewReducer(ctx.Card ?? throw new InvalidOperationException("A reducer needs the discovered card."), ctx.Request.CaseId, ctx.Request.TurnId);

    /// <summary>A reducer whose handle template carries this conversation's facts. The task id stays blank until the first snapshot.</summary>
    /// <param name="card">The discovered card.</param>
    /// <param name="caseId">The case id, when any.</param>
    /// <param name="turnId">The turn id, when any.</param>
    public WorkIqTaskReducer NewReducer(RawAgentCard card, string? caseId, string? turnId) =>
        new(Build(card, "", null), _clock, _diagnostics, caseId, turnId);

    /// <summary>The snapshot of a task read outside any turn, with this conversation's handle and the read's capture.</summary>
    /// <param name="card">The discovered card.</param>
    /// <param name="task">The task as read.</param>
    /// <param name="source">Where it was read.</param>
    /// <param name="capture">The read's capture.</param>
    public WorkIqTaskSnapshot SnapshotOf(RawAgentCard card, AgentTask task, WorkIqSnapshotSource source, RawCaptureRef capture)
    {
        var reducer = NewReducer(card, caseId: null, turnId: null);
        reducer.Load(task, source, null, capture);
        var snapshot = reducer.Snapshot() ?? throw new InvalidOperationException("The task reducer produced no snapshot after loading a task.");
        return snapshot with { Handle = Build(card, task.Id, task.ContextId), Capture = capture };
    }

    private static string ComputePrincipalKey(string tenantId, string appClientId, string scope)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId.Trim()}|{appClientId.Trim()}|{scope.Trim()}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
