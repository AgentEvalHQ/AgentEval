// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The diagnostics seam of <see cref="WorkIqA2AClient"/>: every discovery, send, stream, get, cancel and subscribe
/// emits trace events (<c>discovery.*</c>, <c>rpc.sent</c>, <c>rpc.response</c>, <c>rpc.error</c>, <c>task.state</c>)
/// through this object. Defaults to <see cref="WorkIqDiagnostics.None"/>; the constructor (lane A, M10) sets it
/// from the factory. Owned by M16.
/// </summary>
internal sealed partial class WorkIqA2AClient
{
    /// <summary>The diagnostics every operation of this client traces through; never null.</summary>
    internal WorkIqDiagnostics Diagnostics { get; init; } = WorkIqDiagnostics.None;
}
