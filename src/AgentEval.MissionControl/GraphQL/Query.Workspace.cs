// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MissionControl.Services;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using HotChocolate.CostAnalysis.Types;
using HotChocolate.Types;

namespace AgentEval.MissionControl.GraphQL;

public sealed partial class Query
{
    /// <summary>Evidence availability is independent of whether solution configuration exists.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<bool> HasRunEvidence([Service] IWorkspaceRunSource source, CancellationToken ct = default) =>
        (await source.ReadAsync(ct)).Count > 0;

    /// <summary>Lists configured subjects and subjects observed in verified evidence.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<IReadOnlyList<WorkspaceSubject>> Subjects([Service] IOutputStoreReader store,
        [Service] IWorkspaceRunSource source, AefSubjectKind? kind = null, CancellationToken ct = default)
    {
        var runs = await source.ReadAsync(ct);
        var subjects = runs.GroupBy(r => r.Evidence.Info.SubjectRef, StringComparer.Ordinal).ToDictionary(g => g.Key, g =>
        {
            var ordered = g.OrderByDescending(r => r.Evidence.StartedAt).ThenBy(r => r.SourceId, StringComparer.Ordinal).ToArray();
            return new WorkspaceSubject(WorkspaceRunProjector.Subject(ordered[0].Evidence),
                ordered.Any(r => r.Evidence.StartedAt is null) ? null : WorkspaceRunProjector.Summary(ordered[0]));
        }, StringComparer.Ordinal);
        if (store.IsAvailable)
            await foreach (var item in store.ListSubjectsAsync(ct: ct))
            {
                var identity = item.Identity;
                var subjectKind = identity.Kind == SubjectKind.Workflow ? AefSubjectKind.Workflow : AefSubjectKind.Agent;
                var subjectRef = AefEvalWriter.Ref(identity.Kind == SubjectKind.Workflow ? "workflow" : "agent", identity.Name);
                // Inventory describes current configuration, never the environment of a past execution.
                subjects.TryAdd(subjectRef, new(new(subjectKind, identity.Name, subjectRef, identity.Version,
                    identity.Framework, identity.ModelId, identity.SourceProject, identity.SourcePath), null));
            }
        return subjects.Values.Where(s => kind is null || s.Identity.Kind == kind)
            .OrderBy(s => s.Identity.Name, StringComparer.Ordinal).ThenBy(s => s.Identity.Kind).ToArray();
    }

    /// <summary>Paginated configured and observed subjects.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<Connection<WorkspaceSubject>> SubjectsConnection([Service] IOutputStoreReader store,
        [Service] IWorkspaceRunSource source, AefSubjectKind? kind = null, int? first = null, string? after = null,
        CancellationToken ct = default) => Pagination.Paginate(await Subjects(store, source, kind, ct), first, after);

    /// <summary>Returns a configured or observed subject.</summary>
    [UseRequestScope]
    public async Task<WorkspaceSubject?> Subject([Service] IOutputStoreReader store, [Service] IWorkspaceRunSource source,
        AefSubjectKind kind, string name, string? subjectRef = null, CancellationToken ct = default) =>
        (await Subjects(store, source, kind, ct)).SingleOrDefault(s => s.Identity.Name == name
            && (subjectRef is null || s.Identity.Ref == subjectRef));

    /// <summary>Recent source-qualified runs. Runs with unknown execution times are ordered last, without invented dates.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<IReadOnlyList<WorkspaceRunPointer>> RecentRuns([Service] IWorkspaceRunSource source,
        int count = 50, CancellationToken ct = default) => (await source.ReadAsync(ct))
            .OrderByDescending(r => r.Evidence.StartedAt).ThenBy(r => r.SourceId, StringComparer.Ordinal)
            .Take(Math.Clamp(count, 1, 500)).Select(WorkspaceRunProjector.Pointer).ToArray();

    /// <summary>Reads one verified run, refusing ambiguous IDs unless sourceId is supplied.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<WorkspaceRunView?> Run([Service] IWorkspaceRunSource source, string runId,
        string? sourceId = null, CancellationToken ct = default) =>
        WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId) is { } run ? WorkspaceRunProjector.Run(run) : null;

    /// <summary>Artifacts belonging to the selected run, with captured-content availability.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<IReadOnlyList<WorkspaceArtifact>> RunArtifacts([Service] IWorkspaceRunSource source, string runId,
        string? sourceId = null, CancellationToken ct = default) =>
        WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId) is { } run ? WorkspaceArtifactProjector.Artifacts(run) : [];

    /// <summary>Recorded outcome, observed root census and available totals.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<WorkspaceRunSummary?> RunSummary([Service] IWorkspaceRunSource source, string runId,
        string? sourceId = null, CancellationToken ct = default) =>
        WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId) is { } run ? WorkspaceRunProjector.Summary(run) : null;

    /// <summary>Final root results with nullable measurements; trials and nested results remain available in the evidence reader.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<IReadOnlyList<WorkspaceScenario>> Scenarios([Service] IWorkspaceRunSource source, string runId,
        string? sourceId = null, CancellationToken ct = default) =>
        WorkspaceRunSource.Find(await source.ReadAsync(ct), runId, sourceId) is { } run ? WorkspaceRunProjector.Scenarios(run) : [];

    /// <summary>Paginated final root results.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<Connection<WorkspaceScenario>> ScenarioResultsConnection([Service] IWorkspaceRunSource source,
        string runId, int? first = null, string? after = null, string? sourceId = null, CancellationToken ct = default) =>
        Pagination.Paginate((await Scenarios(source, runId, sourceId, ct)).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(), first, after);

    /// <summary>One final root. Duplicate case identities require selecting a resultId.</summary>
    [UseRequestScope]
    [Cost(100.0)]
    public async Task<WorkspaceScenario?> Scenario([Service] IWorkspaceRunSource source, string runId, string scenarioId,
        string? sourceId = null, string? resultId = null, CancellationToken ct = default) =>
        (await Scenarios(source, runId, sourceId, ct)).SingleOrDefault(s => s.Id == scenarioId && (resultId is null || s.ResultId == resultId));
}
