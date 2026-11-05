// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Decodes the diagnostic artifacts observed in the native evaluator 1.18.0 (experimental, not a documented
/// contract): <c>Developer Info</c> → tool selections and executions (<c>responseStatus</c> integer only;
/// missing args/results/ids stay <see cref="FieldState.Missing"/>), <c>Retrieval Info</c> → retrieval executions
/// (missing status is not success; <c>totalResultCount</c> is not the returned count; unranked hits kept), and an
/// input-required data part → approval intents (every proposed call kept, no name de-duplication, raw argument
/// text verbatim). Artifacts are found by media type and name; the first dictionary-valued data part is used;
/// several same-named artifacts yield <see cref="EvidenceValidation.Conflicting"/>, never last-wins.
/// </summary>
internal static class Native118DiagnosticsDecoder
{
    /// <summary>The decoded diagnostic channels of one task snapshot.</summary>
    /// <param name="ToolSelections">Reported tool selections.</param>
    /// <param name="ToolExecutions">Reported tool executions.</param>
    /// <param name="RetrievalExecutions">Reported retrieval executions.</param>
    /// <param name="ApprovalIntents">Proposed invocations awaiting consent.</param>
    /// <param name="Issues">Decoder-level issues.</param>
    internal sealed record Result(
        EvidenceSet<ToolSelectionObservation> ToolSelections,
        EvidenceSet<ToolExecutionObservation> ToolExecutions,
        EvidenceSet<RetrievalExecutionObservation> RetrievalExecutions,
        EvidenceSet<ApprovalIntentObservation> ApprovalIntents,
        ImmutableArray<EvidenceIssue> Issues);

    /// <summary>Decodes the diagnostic artifacts and data parts of one task snapshot.</summary>
    /// <param name="task">The final task snapshot.</param>
    /// <param name="captureId">The capture the snapshot was read from (for source pointers).</param>
    public static Result Decode(WorkIqTaskSnapshot task, string captureId) =>
        throw new NotImplementedException("TODO(M23)");
}
