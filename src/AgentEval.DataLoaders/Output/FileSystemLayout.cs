// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

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
    /// <summary>
    /// Canonical subject/segment sanitizer used for all on-disk path segments. Promoted to
    /// public (MNT-01) so CLI commands deriving report paths use the SAME rule as the store —
    /// rewriting cross-platform-invalid chars (&lt;&gt;:"|?*/\) and hash-appending lossy names —
    /// instead of weaker copy-pasted variants that produced divergent paths.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be null/empty/whitespace.", nameof(name));
        // Windows superset of invalid filename chars — Linux's
        // Path.GetInvalidFileNameChars() returns only {'\0','/'}, so a colon
        // (or '<>"|?*') sails through on Linux despite being illegal on
        // Windows/macOS resource forks. Harden cross-platform.
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '/', '\\', '<', '>', ':', '"', '|', '?', '*' })
            .ToArray();
        var rewrote = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        var trimmed = rewrote.Trim('.', ' ');
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException(
                $"Name '{name}' sanitises to empty — it must contain at least one non-dot/space character that survives sanitisation.", nameof(name));

        // Round-trip protection: if sanitisation was lossy (any invalid
        // character was replaced or any leading/trailing dot/space was
        // stripped), append a short hash of the ORIGINAL name so names that
        // collide under sanitisation (e.g. "Foo/Bar" + "Foo-Bar" both produce
        // "Foo-Bar") map to distinct on-disk folders. The hash is an 8-byte
        // SHA-256 prefix in hex, deterministic across processes. Phase-7 Task
        // 7.3: bumped from 4→8 bytes (8 hex chars → 16 hex chars). 4-byte
        // prefix had ~65k possible values, so two distinct names sharing a
        // sanitised stem hit ~50% birthday collision at ~300 entries; 8-byte
        // prefix lifts that to ~5 billion — far beyond any realistic .agenteval/
        // subject count.
        if (trimmed == name)
            return trimmed;
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return $"{trimmed}__{Convert.ToHexString(hashBytes, 0, 8).ToLowerInvariant()}";
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
        // Length cap — NTFS path-component limit is 255. Defence-in-depth
        // against DoS-style fat segments.
        if (segment.Length > 255) return false;
        // Compatibility decomposition: catches NFKC-equivalents that would
        // collapse later (zero-width chars, fullwidth lookalikes, etc.). We
        // re-run the prohibited-substring checks against BOTH the raw and
        // normalised forms so an attacker can't smuggle traversal markers
        // through a non-canonical encoding.
        var normalised = segment.IsNormalized(NormalizationForm.FormKC)
            ? segment
            : segment.Normalize(NormalizationForm.FormKC);

        foreach (var s in new[] { segment, normalised })
        {
            if (s is "." or "..") return false;
            if (s.Contains("..", StringComparison.Ordinal)) return false;
            if (s.Contains('/') || s.Contains('\\')) return false;
            // Unicode slash lookalikes — even after NFKC, exotic codepoints
            // can leak through. Reject upfront.
            if (s.Contains('／') || s.Contains('∕')) return false;
            // Trailing '.' / ' ' silently strip on NTFS ("foo." opens "foo"),
            // creating a write-vs-read alias hazard.
            if (s.EndsWith('.') || s.EndsWith(' ')) return false;
        }

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
            // Bidirectional / formatting controls (RTL/LTR overrides, ZWSP,
            // variation selectors). They're not `IsControl` but are still
            // homograph-attack vectors.
            if (c is '​' or '‎' or '‮' or '️') return false;
        }

        // Reject Windows reserved device names — CON, PRN, NUL, AUX, COM1-9,
        // LPT1-9, plus the lesser-known CONIN$ / CONOUT$ / CLOCK$. Windows
        // treats `CON.json` as the device too, so we match the stem before
        // the first '.'. Trailing dot/space inside the stem ("CON .json")
        // is also stripped by NTFS — TrimEnd ensures lookup catches that.
        var stem = segment;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        stem = stem.TrimEnd('.', ' ');
        if (s_reservedDeviceNames.Contains(stem)) return false;
        return true;
    }

    private static readonly HashSet<string> s_reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "NUL", "AUX",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "CONIN$", "CONOUT$", "CLOCK$",
    };
}
