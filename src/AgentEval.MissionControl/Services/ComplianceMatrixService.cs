// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.MissionControl.Services;

/// <summary>Projects verified compliance reports into the existing overview, matrix and detail views.</summary>
public sealed class ComplianceMatrixService
{
    private readonly WorkspaceComplianceSource _source;

    /// <summary>Creates the report projections; legacy stores supply converter inputs only.</summary>
    public ComplianceMatrixService(IOutputStoreReader store, IWorkspaceRunSource? source = null)
    {
        _source = new(store, source ?? new WorkspaceRunSource(store, new AefWorkspaceEvidenceReader()));
    }

    /// <summary>Lists regulations with captured reports, counting subject kind and name together.</summary>
    public async Task<IReadOnlyList<ComplianceRegulationSummary>> ListRegulationsAsync(CancellationToken ct = default) =>
        (await _source.ReadAsync(ct)).GroupBy(r => r.Evidence.Regulation, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var latest = g.OrderByDescending(r => r.Evidence.GeneratedAt)
                .ThenBy(r => r.Evidence.Subject.Kind).ThenBy(r => r.Evidence.Subject.Name, StringComparer.Ordinal).First();
            _source.EnsureLatestAvailable(latest);
            return new ComplianceRegulationSummary(g.Key, g.Select(r => (r.Evidence.Subject.Kind, r.Evidence.Subject.Name)).Distinct().Count(),
                g.Count(), latest.Evidence.GeneratedAt, latest.Evidence.Summary.OverallStatus);
        }).OrderBy(r => r.Regulation, StringComparer.Ordinal).ToArray();

    /// <summary>Uses the latest complete report for each subject, with explicit custody warnings.</summary>
    public async Task<ComplianceMatrix> BuildMatrixAsync(string regulation, CancellationToken ct = default)
    {
        var reports = (await _source.ReadAsync(ct)).Where(r => SameRegulation(r.Evidence.Regulation, regulation))
            .GroupBy(r => (r.Evidence.Subject.Kind, r.Evidence.Subject.Name)).Select(Latest).ToArray();
        if (reports.Length == 0) return new(regulation, [], [], [], false, null);
        var controls = reports.SelectMany(r => r.Evidence.Controls).GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => new ComplianceMatrixControl(g.Key, g.First().Title)).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var subjects = reports.Select(r => new ComplianceMatrixSubject(r.Evidence.Subject.Name, r.Evidence.Subject.Kind))
            .OrderBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var cells = reports.SelectMany(r => r.Evidence.Controls.Select(c => new ComplianceMatrixCell(
            r.Evidence.Subject.Name, c.Id, c.Status, c.PassRate, r.Evidence.GeneratedAt, r.Evidence.SourceRun.RunId,
            r.Timestamp, c.RegressedFromBaseline, r.CustodyWarning is null, r.CustodyWarning)
            { SubjectKind = r.Evidence.Subject.Kind })).ToArray();
        return new(regulation, subjects, controls, cells, reports.All(r => r.CustodyWarning is null),
            reports.Max(r => r.Evidence.GeneratedAt));
    }

    /// <summary>Selects one report using the same timestamp and subject identity exposed by its matrix cell.</summary>
    public async Task<ComplianceEvidenceWithChain?> ReadEvidenceAsync(string regulation, SubjectKind kind,
        string name, string timestamp, CancellationToken ct = default)
    {
        var reports = (await _source.ReadAsync(ct)).Where(r => SameRegulation(r.Evidence.Regulation, regulation)
            && r.Evidence.Subject.Kind == kind && r.Evidence.Subject.Name == name && r.Timestamp == timestamp).ToArray();
        if (reports.Length > 1) throw WorkspaceComplianceSource.Bad("Multiple reports share the selected subject and timestamp.");
        return reports.SingleOrDefault() is { } report
            ? new(report.Evidence, report.CustodyWarning is null, report.CustodyWarning)
                { SourceId = report.SourceId, ContainerRunId = report.SourceId is null ? null : report.Container.RunId } : null;
    }

    private WorkspaceComplianceReport Latest(IEnumerable<WorkspaceComplianceReport> reports)
    {
        var ordered = reports.OrderByDescending(r => r.Evidence.GeneratedAt).ToArray();
        if (ordered.Length > 1 && ordered[0].Evidence.GeneratedAt == ordered[1].Evidence.GeneratedAt)
            throw WorkspaceComplianceSource.Bad("Multiple reports share the latest evidence time.");
        _source.EnsureLatestAvailable(ordered[0]);
        return ordered[0];
    }
    private static bool SameRegulation(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
