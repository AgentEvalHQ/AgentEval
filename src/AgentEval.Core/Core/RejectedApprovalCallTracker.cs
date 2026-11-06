// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Reads one run's contents in order and recognises the parts of a tool call whose approval was rejected: the
/// <see cref="FunctionCallContent"/> that <c>FunctionInvokingChatClient</c> re-creates after a
/// <see cref="ToolApprovalResponseContent"/> that is not approved (same <c>CallId</c> and name), and that call's
/// generated <see cref="FunctionResultContent"/>. The tool did not run.
/// </summary>
/// <remarks>
/// <para>
/// MAF 1.25+ (#9152) returns this shape on the run after an approval request that nobody answered: the generated
/// rejection first, then the re-created call, then a "rejected" result. A decision the caller sends as input is not
/// in the response; code that has it (the workflow bridge, which sends a responder's answer itself) passes it here
/// before the run's contents.
/// </para>
/// <para>
/// The one rule for this shape. Used by the default path of <see cref="ToolUsageExtractor"/>, both streaming adapters,
/// <c>TraceRecordingAgent</c>, <c>AgentBoundaryTraceBuilder</c> and the MAF workflow bridge. It keys on the decision's
/// <c>CallId</c> and name, never on the result text. Approved decisions and decisions with no <c>CallId</c> are
/// ignored. One rejected decision hides one re-created call. One instance per run; not thread-safe.
/// </para>
/// </remarks>
internal sealed class RejectedApprovalCallTracker
{
    // Rejected decisions whose re-created call has not been read yet.
    private readonly HashSet<(string CallId, string Name)> _rejected = new();

    // Call ids of re-created calls already read, each waiting for its generated result.
    private readonly HashSet<string> _awaitingResult = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the next content. Returns <see langword="true"/> when it is the call re-created for a rejected decision
    /// read earlier, or that call's result. A result also returns <see langword="true"/> when its rejected decision
    /// was read but no re-created call came between them (for example, the call was read before the decision).
    /// Neither may be reported as an executed or successful tool call. A decision itself returns
    /// <see langword="false"/>, so a caller that counts decisions still sees it.
    /// </summary>
    /// <param name="content">The next content of the run.</param>
    public bool IsRejectedCallPart(AIContent content)
    {
        switch (content)
        {
            case ToolApprovalResponseContent { Approved: false, ToolCall: FunctionCallContent decided }
                when !string.IsNullOrEmpty(decided.CallId):
                _rejected.Add((decided.CallId, decided.Name));
                return false;

            case FunctionCallContent call
                when !string.IsNullOrEmpty(call.CallId) && _rejected.Remove((call.CallId, call.Name)):
                _awaitingResult.Add(call.CallId);
                return true;

            case FunctionResultContent result when !string.IsNullOrEmpty(result.CallId):
                return _awaitingResult.Remove(result.CallId)
                    || _rejected.RemoveWhere(d => string.Equals(d.CallId, result.CallId, StringComparison.Ordinal)) > 0;

            default:
                return false;
        }
    }
}
