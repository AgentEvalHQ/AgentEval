// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Pure path-resolution helper for the canonical <c>.agenteval/</c> folder layout.
/// Stateless beyond the root path. Public so Mission Control's read-only binary
/// REST endpoints (trace / report / PDF) can resolve paths without re-implementing
/// the layout convention.
/// </summary>
public sealed class FileSystemLayout
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

    /// <summary>
    /// Strictly validates a user-supplied URL route or GraphQL argument that is
    /// about to be concatenated into a filesystem path (e.g. <c>runId</c>,
    /// <c>regulation</c>, <c>timestamp</c>, <c>format</c>). Returns <c>true</c>
    /// only when the segment is safe — non-empty, no directory separators, no
    /// <c>..</c> traversal markers, no control characters, no platform-invalid
    /// filename characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Sanitize"/> which silently <em>rewrites</em> a
    /// user-friendly subject name into a safe filename (lossy), this helper is
    /// strict: it returns <c>false</c> rather than scrub the input. Callers
    /// (Mission Control's REST + GraphQL endpoints) MUST validate untrusted
    /// route segments with this helper before they reach <c>Path.Combine</c>.
    /// Failing to do so opens a directory-traversal attack surface — e.g.
    /// <c>GET /api/v1/runs/..%2F..%2Fetc%2Fpasswd/trace</c>.
    /// </para>
    /// <para>
    /// Trust boundary at v1 is "single user, local Mode A" so impact is bounded,
    /// but this becomes a CVE-class hazard the moment Mission Control runs in
    /// Mode B (multi-tenant workspace aggregator) — fix it once, here.
    /// </para>
    /// </remarks>
    public static bool IsSafePathSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        if (segment is "." or "..") return false;
        if (segment.Contains("..", StringComparison.Ordinal)) return false;
        if (segment.Contains('/') || segment.Contains('\\')) return false;
        // Unicode lookalikes that some tools later normalize to '/' (U+FF0F
        // fullwidth solidus, U+2215 division slash). Reject them upfront so
        // a path that "looks safe" today doesn't escape after a normalization
        // pass tomorrow.
        if (segment.Contains('／') || segment.Contains('∕')) return false;
        // Trailing '.' / ' ' silently strip on NTFS ("foo." opens "foo"),
        // creating a write-vs-read alias hazard.
        if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
        // Windows superset of invalid filename chars (Linux's
        // GetInvalidFileNameChars returns only {'\0','/'}; harden for shared
        // workspaces accessed cross-platform).
        const string winInvalid = "<>:\"|?*";
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in segment)
        {
            if (Array.IndexOf(invalid, c) >= 0) return false;
            if (winInvalid.IndexOf(c) >= 0) return false;
            if (char.IsControl(c)) return false;
        }
        // Reject Windows reserved device names — CON, PRN, NUL, AUX, COM1-9,
        // LPT1-9. Windows treats `CON.json` as the device too, so we match
        // the stem before the first '.'.
        var stem = segment;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        if (s_reservedDeviceNames.Contains(stem)) return false;
        return true;
    }

    private static readonly HashSet<string> s_reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "NUL", "AUX",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
