// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>An evidence descriptor with verified captured-content availability. External locations are not followed or exposed.</summary>
public sealed record WorkspaceArtifact(string EvidenceId, string Kind, string? Description, bool Captured,
    string? ReportFormat, bool LegacyTrace);

/// <summary>Subject identity from evidence or configured inventory, including native AEF kinds.</summary>
public sealed record WorkspaceSubjectIdentity(AefSubjectKind Kind, string Name, string Ref, string? Version = null,
    string? Framework = null, string? ModelId = null, string? SourceProject = null, string? SourcePath = null)
{
    /// <summary>The recorded kind, including unfamiliar values permitted by AEF forward compatibility.</summary>
    public string RawKind { get; init; } = Kind.ToString().ToLowerInvariant();
}
/// <summary>A subject with its latest observed execution, if any.</summary>
public sealed record WorkspaceSubject(WorkspaceSubjectIdentity Identity, WorkspaceRunSummary? LastRun);
/// <summary>One source-qualified recent run. Unknown execution times and outcomes remain null.</summary>
public sealed record WorkspaceRunPointer(string SourceId, string RunId, string SubjectName, AefSubjectKind Kind,
    string? Verdict, DateTimeOffset? Timestamp, double? Score, double? DurationMs, double? EstimatedCost)
{
    /// <summary>Exact subject reference; display names are not identifiers.</summary>
    public required string SubjectRef { get; init; }
    /// <summary>The recorded kind, without enum coercion.</summary>
    public required string RawKind { get; init; }
}
/// <summary>Presentation view of a verified run without fabricated legacy manifest fields.</summary>
public sealed record WorkspaceRunView(string SourceId, string SchemaVersion, string? ContentHash,
    WorkspaceSolution? Solution, WorkspaceSubjectIdentity Subject, WorkspaceExecution Run, WorkspaceGit? Git,
    WorkspaceEnvironment? Environment, WorkspaceProducer? AgentEval, AefRunReadInfo Evidence);
/// <summary>Recorded solution metadata, when supplied.</summary>
public sealed record WorkspaceSolution(string? Id, string? Name);
/// <summary>Execution observations, distinct from the integrity and completion of the AEF container.</summary>
public sealed record WorkspaceExecution(string RunId, DateTimeOffset? Timestamp, TimeSpan? Duration,
    string? Verdict, string? Kind, int ScenarioCount, string? Harness, int? Seed);
/// <summary>Recorded Git metadata; unknown cleanliness is not clean.</summary>
public sealed record WorkspaceGit(string? Commit, string? Branch, bool? Dirty);
/// <summary>Recorded environment metadata; unknown CI state is not false.</summary>
public sealed record WorkspaceEnvironment(string? Os, string? DotnetVersion, bool? Ci);
/// <summary>Recorded tool version.</summary>
public sealed record WorkspaceProducer(string? Version);
/// <summary>Recorded run outcome and observed root-result census.</summary>
public sealed record WorkspaceRunSummary(string RunId, string? Verdict, WorkspaceRunStats Stats, WorkspaceRunCost? Cost)
{
    /// <summary>Exact observed root states, including unfamiliar states; these counts never imply planned coverage.</summary>
    public required IReadOnlyList<WorkspaceStateCount> States { get; init; }
}
/// <summary>Count of observed final roots in one recorded state.</summary>
public sealed record WorkspaceStateCount(string State, int Count);
/// <summary>Observed final root counts; these do not assert the planned case count or completion.</summary>
public sealed record WorkspaceRunStats(int Total, int Passed, int Failed, int Warnings, int Skipped);
/// <summary>Recorded totals; absent costs or token counts remain null.</summary>
public sealed record WorkspaceRunCost(double? EstimatedCost, long? PromptTokens, long? CompletionTokens);
/// <summary>One final root result. A scored result without a decision has no pass/fail boolean.</summary>
public sealed record WorkspaceScenario(string Id, string ResultId, string Name, string State, bool? Passed,
    double? Score, string? Measurement, TimeSpan? Duration, double? EstimatedCost);
