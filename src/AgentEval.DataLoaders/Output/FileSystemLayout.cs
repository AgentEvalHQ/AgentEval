// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

internal sealed class FileSystemLayout
{
    public string Root { get; }

    public FileSystemLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    // Solution-level
    public string SolutionFile => Path.Combine(Root, "solution.json");
    public string ReadmeFile   => Path.Combine(Root, "README.md");
    public string GitignoreFile => Path.Combine(Root, ".gitignore");
    public string ConfigDir    => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string ThresholdsDir => Path.Combine(ConfigDir, "thresholds");
    public string ThresholdsFile(string subjectName) => Path.Combine(ThresholdsDir, $"{Sanitize(subjectName)}.json");

    // Subjects
    public string SubjectsDir => Path.Combine(Root, "subjects");
    public string SubjectKindDir(SubjectKind k) => Path.Combine(SubjectsDir, k.Folder());
    public string SubjectDir(SubjectIdentity s) => Path.Combine(SubjectKindDir(s.Kind), Sanitize(s.Name));
    public string SubjectFile(SubjectIdentity s) => Path.Combine(SubjectDir(s), "subject.json");
    public string BaselineFile(SubjectIdentity s) => Path.Combine(SubjectDir(s), "baseline.json");
    public string BaselinesDir(SubjectIdentity s) => Path.Combine(SubjectDir(s), "baselines");
    public string PinnedBaselineFile(SubjectIdentity s, string version) => Path.Combine(BaselinesDir(s), $"{version}.json");
    public string HistoryFile(SubjectIdentity s) => Path.Combine(SubjectDir(s), "history.jsonl");

    // Runs
    public string RunsDir(SubjectIdentity s) => Path.Combine(SubjectDir(s), "runs");
    public string RunDir(SubjectIdentity s, string runId) => Path.Combine(RunsDir(s), runId);
    public string ManifestFile(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "manifest.json");
    public string SummaryFile(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "summary.json");
    public string MemoryFile(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "memory.json");
    public string ScenariosDir(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "scenarios");
    public string ScenarioFile(SubjectIdentity s, string runId, string scenarioId) => Path.Combine(ScenariosDir(s, runId), $"{Sanitize(scenarioId)}.json");
    public string TracesDir(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "traces");
    public string TraceFile(SubjectIdentity s, string runId) => Path.Combine(TracesDir(s, runId), "agent-trace.json");
    public string ReportsDir(SubjectIdentity s, string runId) => Path.Combine(RunDir(s, runId), "reports");
    public string ReportFile(SubjectIdentity s, string runId, string format) => Path.Combine(ReportsDir(s, runId), $"report.{format}");

    // Compliance
    public string ComplianceDir(string regulation) => Path.Combine(Root, "compliance", regulation);
    public string ComplianceSubjectDir(string regulation, SubjectIdentity s) => Path.Combine(ComplianceDir(regulation), Sanitize(s.Name));
    public string ComplianceTimestampDir(string regulation, SubjectIdentity s, string ts) => Path.Combine(ComplianceSubjectDir(regulation, s), ts);
    public string ComplianceEvidenceFile(string regulation, SubjectIdentity s, string ts) => Path.Combine(ComplianceTimestampDir(regulation, s, ts), "evidence.json");
    public string ComplianceReportPdf(string regulation, SubjectIdentity s, string ts) => Path.Combine(ComplianceTimestampDir(regulation, s, ts), "report.pdf");

    // Red-team
    public string RedTeamDir(string campaignName, string ts) => Path.Combine(Root, "red-team", $"{Sanitize(campaignName)}_{ts}");
    public string RedTeamManifestFile(string campaignName, string ts) => Path.Combine(RedTeamDir(campaignName, ts), "manifest.json");
    public string RedTeamFindingsFile(string campaignName, string ts) => Path.Combine(RedTeamDir(campaignName, ts), "findings.json");
    public string RedTeamReportsDir(string campaignName, string ts) => Path.Combine(RedTeamDir(campaignName, ts), "reports");

    // Cross-cutting indices
    public string RunsIndexDir => Path.Combine(Root, "runs-index");
    public string RecentRunsFile => Path.Combine(RunsIndexDir, "recent.jsonl");
    public string MasterIndexFile => Path.Combine(RunsIndexDir, "runs.index.jsonl");

    // Projects index
    public string ProjectsDir => Path.Combine(Root, "projects");
    public string ProjectFile(string projectName) => Path.Combine(ProjectsDir, Sanitize(projectName), "project.json");
    public string DeclaresFile(string projectName) => Path.Combine(ProjectsDir, Sanitize(projectName), "declares.jsonl");

    // Portal
    public string PortalDir => Path.Combine(Root, "portal");
    public string TargetsFile => Path.Combine(PortalDir, "targets.json");
    public string OutboxFile => Path.Combine(PortalDir, "outbox.jsonl");
    public string SyncedFile => Path.Combine(PortalDir, "synced.jsonl");

    // Helpers
    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }
}
