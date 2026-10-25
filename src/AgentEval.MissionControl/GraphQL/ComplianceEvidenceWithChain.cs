// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>A recorded report with its verified container custody warning and available workspace location.</summary>
/// <param name="Evidence">The recorded report; SourceRun remains its original, possibly orphaned reference.</param>
/// <param name="ChainValid">True for a report in an intact native AEF container. Signature trust is not checked.</param>
/// <param name="ChainBreakReason">Import, missing-source or unsealed-evidence warning. Conflicting/invalid evidence is refused.</param>
public sealed record ComplianceEvidenceWithChain(
    ComplianceEvidence Evidence,
    bool ChainValid,
    string? ChainBreakReason)
{
    /// <summary>The verified workspace source holding this report, or null for a standalone orphan conversion.</summary>
    public string? SourceId { get; init; }
    /// <summary>The canonical containing run ID, distinct from the report's recorded SourceRun reference.</summary>
    public string? ContainerRunId { get; init; }
}
