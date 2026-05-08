// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Uniquely identifies an agent or workflow subject within a solution.</summary>
public sealed record SubjectIdentity(
    SubjectKind Kind,
    string Name,
    string? SourceProject = null,
    string? SourcePath = null,
    string? Version = null,
    string? ModelId = null,
    string? Framework = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>The qualified identifier for this subject (v1: same as Name).</summary>
    public string QualifiedId => Name;
}
