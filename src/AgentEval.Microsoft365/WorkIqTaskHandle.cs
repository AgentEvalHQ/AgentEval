// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365;

/// <summary>
/// Everything needed to address an existing remote task again (<c>GetTask</c>, <c>CancelTask</c>,
/// <c>SubscribeToTask</c>) without re-sending its input: the endpoint and protocol version it lives on, the
/// opaque agent and routing tenant, and the logical principal it was created for. It never carries a token.
/// </summary>
/// <param name="Endpoint">The selected JSON-RPC interface URL the task was created on.</param>
/// <param name="ProtocolVersion">The A2A protocol version negotiated (<c>1.0</c>).</param>
/// <param name="AgentId">The opaque agent id the task was routed to; null for the default agent.</param>
/// <param name="TaskId">The server-generated task id.</param>
/// <param name="ContextId">The server conversation context id, when returned.</param>
/// <param name="RoutingTenant">The opaque A2A <c>tenant</c> routing value of the selected interface, when any; not the Entra tenant id.</param>
/// <param name="PrincipalKey">A stable, non-secret key of the signed-in principal + tenant the task belongs to (for example a hash of tenant|app|account); never derived from a token.</param>
public sealed record WorkIqTaskHandle(
    Uri Endpoint,
    string ProtocolVersion,
    string? AgentId,
    string TaskId,
    string? ContextId,
    string? RoutingTenant,
    string PrincipalKey);
