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
    /// <remarks>
    /// Computed alias for callers that want a "stable id" handle independent of how
    /// v2 may split <see cref="Name"/> from a fully-qualified scope. Not persisted —
    /// the on-disk JSON shape is governed by <c>subject.schema.json</c> /
    /// <c>evidence.schema.json</c>, which require <c>kind</c>+<c>name</c> only.
    /// <para>
    /// Phase-5 Task 5.2: marked <c>internal</c> so Hot Chocolate's default
    /// public-property convention does not auto-bind it into the v1 GraphQL
    /// surface. Pulling <c>HotChocolate.GraphQLIgnore</c> into
    /// <c>AgentEval.Abstractions</c> would add an undesired transitive package
    /// dependency; <c>internal</c> achieves the same hide on the default convention.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    internal string QualifiedId => Name;
}
