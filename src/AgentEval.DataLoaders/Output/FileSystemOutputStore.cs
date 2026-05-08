// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Output;

/// <summary>File-system-backed implementation of <see cref="IOutputStore"/>.</summary>
public sealed class FileSystemOutputStore : IOutputStore
{
    private readonly FileSystemLayout _layout;
    private readonly JsonSerializerOptions _json;
    private readonly JsonSerializerOptions _jsonl;
    private SolutionInfo? _cachedSolution;

    // Cache for LocateRunAsync — stores (subject, runId) of the most recently located run
    private (SubjectIdentity Subject, string RunId)? _lastLocated;

    public FileSystemOutputStore(string root)
    {
        _layout = new FileSystemLayout(root);
        _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        _jsonl = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    public string? WorkspaceRoot => _layout.Root;
    public bool IsAvailable => File.Exists(_layout.SolutionFile);

    // ─── Solution lifecycle ──────────────────────────────────────────────────

    public async Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default)
    {
        if (_cachedSolution is not null) return _cachedSolution;
        if (!File.Exists(_layout.SolutionFile))
            throw new InvalidOperationException(
                $"No solution.json at {_layout.SolutionFile}. Run `agenteval init` first.");

        var dto = await ReadJsonAsync<SolutionFileV1>(_layout.SolutionFile, ct);
        if (dto is null)
            throw new InvalidOperationException(
                $"No solution.json at {_layout.SolutionFile}. Run `agenteval init` first.");

        _cachedSolution = new SolutionInfo(dto.Id, dto.Name, _layout.Root);
        return _cachedSolution;
    }

    // ─── Subject lifecycle ───────────────────────────────────────────────────

    public async Task<SubjectInfo> EnsureSubjectAsync(SubjectIdentity identity, CancellationToken ct = default)
    {
        var subjectFile = _layout.SubjectFile(identity);
        SubjectFileV1? existing = null;
        if (File.Exists(subjectFile))
            existing = await ReadJsonAsync<SubjectFileV1>(subjectFile, ct);

        var dto = new SubjectFileV1(
            SchemaVersion: "1.0",
            Kind: identity.Kind.ToString().ToLowerInvariant(),
            Name: identity.Name,
            Version: identity.Version ?? existing?.Version,
            Framework: identity.Framework ?? existing?.Framework,
            ModelId: identity.ModelId ?? existing?.ModelId,
            SourceProject: identity.SourceProject ?? existing?.SourceProject,
            SourcePath: identity.SourcePath ?? existing?.SourcePath,
            Tags: identity.Tags ?? existing?.Tags);

        await WriteJsonAsync(subjectFile, dto, ct);

        // Load latest run summary if any
        RunSummary? lastRun = null;
        var runsDir = _layout.RunsDir(identity);
        if (Directory.Exists(runsDir))
        {
            var latestRunId = Directory.GetDirectories(runsDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latestRunId is not null)
                lastRun = await ReadJsonAsync<RunSummary>(_layout.SummaryFile(identity, latestRunId), ct);
        }

        return new SubjectInfo(identity, lastRun);
    }

    public async IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(
        SubjectKind? kind = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var kindsToScan = kind.HasValue
            ? new[] { kind.Value }
            : new[] { SubjectKind.Agent, SubjectKind.Workflow };

        foreach (var k in kindsToScan)
        {
            var kindDir = _layout.SubjectKindDir(k);
            if (!Directory.Exists(kindDir)) continue;

            foreach (var subjectDir in Directory.GetDirectories(kindDir))
            {
                ct.ThrowIfCancellationRequested();
                var subjectFile = Path.Combine(subjectDir, "subject.json");
                if (!File.Exists(subjectFile)) continue;

                var dto = await ReadJsonAsync<SubjectFileV1>(subjectFile, ct);
                if (dto is null) continue;

                if (!Enum.TryParse<SubjectKind>(dto.Kind, ignoreCase: true, out var parsedKind))
                    parsedKind = k;

                var identity = new SubjectIdentity(parsedKind, dto.Name, dto.SourceProject, dto.SourcePath, dto.Version, dto.ModelId, dto.Framework, dto.Tags);
                RunSummary? lastRun = null;

                var runsDir = _layout.RunsDir(identity);
                if (Directory.Exists(runsDir))
                {
                    var latestRunId = Directory.GetDirectories(runsDir)
                        .Select(Path.GetFileName)
                        .Where(n => n is not null)
                        .OrderByDescending(n => n, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (latestRunId is not null)
                        lastRun = await ReadJsonAsync<RunSummary>(_layout.SummaryFile(identity, latestRunId), ct);
                }

                yield return new SubjectInfo(identity, lastRun);
            }
        }
    }

    // ─── Run lifecycle ───────────────────────────────────────────────────────

    public async Task<RunManifest> StartRunAsync(SubjectIdentity subject, RunContext context, CancellationToken ct = default)
    {
        var solution = await EnsureSolutionAsync(ct);
        await EnsureSubjectAsync(subject, ct);

        var runId = GenerateRunId();
        var runDir = _layout.RunDir(subject, runId);
        Directory.CreateDirectory(runDir);

        var manifest = new RunManifest(
            SchemaVersion: "1.0",
            Solution: new SolutionRef(solution.Id, solution.Name, null),
            Subject: new SubjectRef(subject.Kind, subject.Name, subject.Version, subject.Framework, subject.ModelId, subject.SourceProject, subject.SourcePath),
            Run: new RunRef(runId, DateTimeOffset.UtcNow, TimeSpan.Zero, 0, "PENDING", context.Kind, context.EvalProject, context.EvalProjectPath, context.Harness, context.Seed, context.ParentInvocationId),
            Git: GitProbe.Probe(_layout.Root),
            AgentEval: new AgentEvalRef(typeof(FileSystemOutputStore).Assembly.GetName().Version?.ToString() ?? "0.0.0", null),
            Environment: EnvProbe.Probe(),
            ContentHash: "sha256:0000000000000000000000000000000000000000000000000000000000000000");

        await WriteJsonAsync(_layout.ManifestFile(subject, runId), manifest, ct);
        return manifest;
    }

    public async Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default)
    {
        var subject = await LocateRunAsync(runId, ct);
        var path = _layout.ScenarioFile(subject, runId, result.Id);
        await WriteJsonAsync(path, result, ct);
    }

    public async Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default)
    {
        var subject = manifest.Subject.ToIdentity();
        var runId = manifest.Run.RunId;

        await WriteJsonAsync(_layout.SummaryFile(subject, runId), summary, ct);

        var hash = await ContentHasher.HashRunAsync(_layout, subject, runId, ct);
        var updated = manifest with
        {
            ContentHash = $"sha256:{hash}",
            Run = manifest.Run with
            {
                Verdict = summary.Verdict,
                Duration = DateTimeOffset.UtcNow - manifest.Run.Timestamp,
                ScenarioCount = summary.Stats.Total
            }
        };
        await WriteJsonAsync(_layout.ManifestFile(subject, runId), updated, ct);

        var entry = HistoryEntry.From(updated, summary);
        await AppendHistoryEntryAsync(subject, entry, ct);

        await AppendJsonlLineAsync(_layout.RecentRunsFile,
            new RunPointer(runId, subject.Name, summary.Verdict, manifest.Run.Timestamp), ct);
    }

    public async Task AppendTraceAsync(string runId, AgentTrace trace, CancellationToken ct = default)
    {
        var subject = await LocateRunAsync(runId, ct);
        await WriteJsonAsync(_layout.TraceFile(subject, runId), trace, ct);
    }

    // ─── Baselines ───────────────────────────────────────────────────────────

    public async Task SaveBaselineAsync(SubjectIdentity subject, RunSummary summary, string? versionTag = null, CancellationToken ct = default)
    {
        var path = versionTag is not null
            ? _layout.PinnedBaselineFile(subject, versionTag)
            : _layout.BaselineFile(subject);
        await WriteJsonAsync(path, summary, ct);
    }

    public async Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default)
        => await ReadJsonAsync<RunSummary>(_layout.BaselineFile(subject), ct);

    public async Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default)
    {
        var subject = await LocateRunAsync(runId, ct);
        var current = await ReadJsonAsync<RunSummary>(_layout.SummaryFile(subject, runId), ct);
        var baseline = await LoadBaselineAsync(subject, ct);

        if (current is null)
            throw new InvalidOperationException($"Summary not found for run {runId}.");

        if (baseline is null)
            return new BaselineComparison(subject.Name, null, current, new Dictionary<string, double>(), false);

        var deltas = new Dictionary<string, double>();
        foreach (var key in current.Metrics.Keys.Intersect(baseline.Metrics.Keys))
            deltas[key] = current.Metrics[key] - baseline.Metrics[key];

        var anyDecreased = deltas.Values.Any(d => d < 0);
        var verdictWorsened = baseline.Verdict == "PASS" && current.Verdict != "PASS";
        var regressed = anyDecreased && verdictWorsened;

        return new BaselineComparison(subject.Name, baseline, current, deltas, regressed);
    }

    // ─── History ─────────────────────────────────────────────────────────────

    public async Task AppendHistoryEntryAsync(SubjectIdentity subject, HistoryEntry entry, CancellationToken ct = default)
        => await AppendJsonlLineAsync(_layout.HistoryFile(subject), entry, ct);

    public async IAsyncEnumerable<HistoryEntry> GetHistoryAsync(
        SubjectIdentity subject,
        DateRange? range = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var historyFile = _layout.HistoryFile(subject);
        if (!File.Exists(historyFile)) yield break;

        var lines = await File.ReadAllLinesAsync(historyFile, ct);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ct.ThrowIfCancellationRequested();

            HistoryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<HistoryEntry>(line, _jsonl);
            }
            catch
            {
                continue;
            }

            if (entry is null) continue;
            if (range is not null && (entry.Timestamp < range.From || entry.Timestamp > range.To)) continue;
            yield return entry;
        }
    }

    // ─── Compliance ───────────────────────────────────────────────────────────

    public async Task SaveComplianceEvidenceAsync(string regulation, SubjectIdentity subject, ComplianceEvidence evidence, CancellationToken ct = default)
    {
        var ts = evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = _layout.ComplianceEvidenceFile(regulation, subject, ts);
        await WriteJsonAsync(path, evidence, ct);
    }

    public async IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(
        string? regulation = null,
        SubjectIdentity? subject = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var complianceRoot = Path.Combine(_layout.Root, "compliance");
        if (!Directory.Exists(complianceRoot)) yield break;

        var regulationDirs = regulation is not null
            ? new[] { Path.Combine(complianceRoot, regulation) }.Where(Directory.Exists)
            : Directory.GetDirectories(complianceRoot).AsEnumerable();

        foreach (var regDir in regulationDirs)
        {
            ct.ThrowIfCancellationRequested();
            var reg = Path.GetFileName(regDir);

            foreach (var subjectDir in Directory.GetDirectories(regDir))
            {
                var subjectName = Path.GetFileName(subjectDir);
                if (subject is not null && !string.Equals(subjectName,
                    subject.Name.Replace('/', '-').Replace('\\', '-'), StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var tsDir in Directory.GetDirectories(subjectDir))
                {
                    ct.ThrowIfCancellationRequested();
                    var ts = Path.GetFileName(tsDir);
                    var evidenceFile = Path.Combine(tsDir, "evidence.json");
                    if (!File.Exists(evidenceFile)) continue;

                    var evidence = await ReadJsonAsync<ComplianceEvidence>(evidenceFile, ct);
                    if (evidence is null) continue;

                    yield return new ComplianceEvidencePointer(reg!, subjectName!, ts!, evidence.Summary.OverallStatus);
                }
            }
        }
    }

    public async Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default)
        => await ReadJsonAsync<ComplianceEvidence>(_layout.ComplianceEvidenceFile(regulation, subject, timestamp), ct);

    // ─── Red-team ───────────────────────────────────────────────────────────

    public async Task<RedTeamCampaignManifest> StartRedTeamCampaignAsync(RedTeamCampaignContext context, CancellationToken ct = default)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sanitizedName = SanitizeName(context.Name);
        var campaignId = $"{sanitizedName}_{ts}";

        var manifest = new RedTeamCampaignManifest(
            SchemaVersion: "1.0",
            CampaignId: campaignId,
            Name: context.Name,
            StartedAt: DateTimeOffset.UtcNow,
            Targets: context.Targets,
            Mode: context.Mode);

        await WriteJsonAsync(_layout.RedTeamManifestFile(sanitizedName, ts), manifest, ct);
        return manifest;
    }

    public async Task CompleteRedTeamCampaignAsync(string campaignId, RedTeamFindings findings, CancellationToken ct = default)
    {
        // campaignId = "{sanitizedName}_{yyyy-MM-dd_HH-mm-ss}" — trailing 19 chars are the timestamp
        const int tsSuffixLen = 19; // yyyy-MM-dd_HH-mm-ss
        if (campaignId.Length < tsSuffixLen + 1)
            throw new ArgumentException($"Invalid campaignId format: '{campaignId}'.", nameof(campaignId));

        var ts = campaignId[^tsSuffixLen..];
        var nameWithUnderscore = campaignId[..^(tsSuffixLen + 1)];

        // Validate ts looks like a timestamp
        if (ts.Length != tsSuffixLen || !char.IsDigit(ts[0]))
            throw new ArgumentException($"Invalid campaignId format: '{campaignId}'.", nameof(campaignId));

        await WriteJsonAsync(_layout.RedTeamFindingsFile(nameWithUnderscore, ts), findings, ct);
    }

    // ─── Cross-cutting ────────────────────────────────────────────────────────

    public async IAsyncEnumerable<RunPointer> GetRecentRunsAsync(
        int count = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var recentFile = _layout.RecentRunsFile;
        if (!File.Exists(recentFile)) yield break;

        var lines = await File.ReadAllLinesAsync(recentFile, ct);
        var pointers = new List<RunPointer>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var pointer = JsonSerializer.Deserialize<RunPointer>(line, _jsonl);
                if (pointer is not null) pointers.Add(pointer);
            }
            catch { }
        }

        foreach (var pointer in pointers.AsEnumerable().Reverse().Take(count))
        {
            ct.ThrowIfCancellationRequested();
            yield return pointer;
        }
    }

    // ─── Run retrieval ────────────────────────────────────────────────────────

    public async Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
    {
        var subject = await TryLocateRunAsync(runId, ct);
        if (subject is null) return null;
        return await ReadJsonAsync<RunManifest>(_layout.ManifestFile(subject, runId), ct);
    }

    public async Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default)
    {
        var subject = await TryLocateRunAsync(runId, ct);
        if (subject is null) return null;
        return await ReadJsonAsync<RunSummary>(_layout.SummaryFile(subject, runId), ct);
    }

    public async IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subject = await TryLocateRunAsync(runId, ct);
        if (subject is null) yield break;

        var scenariosDir = _layout.ScenariosDir(subject, runId);
        if (!Directory.Exists(scenariosDir)) yield break;

        foreach (var file in Directory.GetFiles(scenariosDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var result = await ReadJsonAsync<ScenarioResult>(file, ct);
            if (result is not null) yield return result;
        }
    }

    public async Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default)
    {
        var subject = await TryLocateRunAsync(runId, ct);
        if (subject is null) return null;
        var traceFile = _layout.TraceFile(subject, runId);
        if (!File.Exists(traceFile)) return null;
        return await ReadJsonAsync<AgentTrace>(traceFile, ct);
    }

    public async IAsyncEnumerable<RunManifest> ListRunsAsync(
        SubjectIdentity subject,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var runsDir = _layout.RunsDir(subject);
        if (!Directory.Exists(runsDir)) yield break;

        foreach (var runDir in Directory.GetDirectories(runsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var runId = Path.GetFileName(runDir);
            if (runId is null) continue;
            var manifest = await ReadJsonAsync<RunManifest>(_layout.ManifestFile(subject, runId), ct);
            if (manifest is not null) yield return manifest;
        }
    }

    // ─── Private helpers ────────────────────────────────────────────────────

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, _json, ct);
    }

    private async Task AppendJsonlLineAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(value, _jsonl);
        await File.AppendAllTextAsync(path, line + "\n", ct);
    }

    private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }

    private async Task<SubjectIdentity> LocateRunAsync(string runId, CancellationToken ct)
    {
        var result = await TryLocateRunAsync(runId, ct);
        if (result is null)
            throw new InvalidOperationException($"Run '{runId}' not found in any subject.");
        return result;
    }

    private async Task<SubjectIdentity?> TryLocateRunAsync(string runId, CancellationToken ct)
    {
        if (_lastLocated.HasValue && _lastLocated.Value.RunId == runId)
            return _lastLocated.Value.Subject;

        foreach (var kind in new[] { SubjectKind.Agent, SubjectKind.Workflow })
        {
            var kindDir = _layout.SubjectKindDir(kind);
            if (!Directory.Exists(kindDir)) continue;

            foreach (var subjectDir in Directory.GetDirectories(kindDir))
            {
                ct.ThrowIfCancellationRequested();
                var runDir = Path.Combine(subjectDir, "runs", runId);
                if (!Directory.Exists(runDir)) continue;

                var subjectFile = Path.Combine(subjectDir, "subject.json");
                if (!File.Exists(subjectFile)) continue;

                var dto = await ReadJsonAsync<SubjectFileV1>(subjectFile, ct);
                if (dto is null) continue;

                if (!Enum.TryParse<SubjectKind>(dto.Kind, ignoreCase: true, out var parsedKind))
                    parsedKind = kind;

                var identity = new SubjectIdentity(parsedKind, dto.Name, dto.SourceProject, dto.SourcePath, dto.Version, dto.ModelId, dto.Framework, dto.Tags);
                _lastLocated = (identity, runId);
                return identity;
            }
        }

        return null;
    }

    private static string GenerateRunId()
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{ts}_{hex}";
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    // Private write-only helper (not yet exposed but included for completeness)
    private async Task WriteSolutionAsync(Guid id, string name, CancellationToken ct)
        => await WriteJsonAsync(_layout.SolutionFile, new SolutionFileV1("1.0", id, name), ct);

    // ─── Private DTOs ────────────────────────────────────────────────────────

    private sealed record SolutionFileV1(string SchemaVersion, Guid Id, string Name);

    private sealed record SubjectFileV1(
        string SchemaVersion,
        string Kind,
        string Name,
        string? Version,
        string? Framework,
        string? ModelId,
        string? SourceProject,
        string? SourcePath,
        IReadOnlyList<string>? Tags);
}
