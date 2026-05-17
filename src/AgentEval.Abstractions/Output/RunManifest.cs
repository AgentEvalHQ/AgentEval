// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Schema-mirroring record for a per-run <c>manifest.json</c> file.</summary>
public sealed record RunManifest(
    string SchemaVersion,
    SolutionRef Solution,
    SubjectRef Subject,
    RunRef Run,
    GitRef Git,
    AgentEvalRef AgentEval,
    EnvRef Environment,
    string ContentHash);

/// <summary>Reference to the solution that owns the run.</summary>
public sealed record SolutionRef(Guid Id, string Name, string? WorkspaceTag);

/// <summary>Reference to the subject (agent or workflow) under evaluation.</summary>
public sealed record SubjectRef(
    SubjectKind Kind,
    string Name,
    string? Version,
    string? Framework,
    string? ModelId,
    string? SourceProject,
    string? SourcePath)
{
    /// <summary>Converts this reference into a <see cref="SubjectIdentity"/>.</summary>
    public SubjectIdentity ToIdentity() =>
        new(Kind, Name, SourceProject, SourcePath, Version, ModelId, Framework);
}

/// <summary>Reference to the specific run execution.</summary>
public sealed record RunRef(
    string RunId,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    int ScenarioCount,
    string Verdict,
    string Kind,
    string? EvalProject,
    string? EvalProjectPath,
    string Harness,
    int? Seed,
    string? ParentInvocationId);

/// <summary>Git state at the time of the run.</summary>
public sealed record GitRef(string? Commit, string? Branch, bool Dirty, string? Tag);

/// <summary>AgentEval tooling version information.</summary>
public sealed record AgentEvalRef(string Version, string? ConfigurationId);

/// <summary>Runtime environment details for the run.</summary>
public sealed record EnvRef(
    string Machine,
    string Os,
    string DotnetVersion,
    bool Ci,
    string? CiSystem,
    string? Runner);
