// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Output;

/// <summary>In-memory implementation of <see cref="IOutputStore"/> suitable for testing.</summary>
public sealed class InMemoryOutputStore : IOutputStore
{
    private readonly object _lock = new();
    private SolutionInfo _solution = new(Guid.NewGuid(), "in-memory", "memory://");

    private readonly Dictionary<(SubjectKind Kind, string Name), SubjectInfo> _subjects = new();
    private readonly Dictionary<string, RunEntry> _runs = new();
    private readonly Dictionary<(SubjectKind Kind, string Name), RunSummary> _baselines = new();
    private readonly Dictionary<(SubjectKind Kind, string Name, string Version), RunSummary> _pinnedBaselines = new();
    private readonly Dictionary<(SubjectKind Kind, string Name), List<HistoryEntry>> _history = new();
    private readonly Dictionary<(string Regulation, SubjectKind Kind, string SubjectName, string Ts), ComplianceEvidence> _complianceEvidence = new();
    private readonly Dictionary<string, (RedTeamCampaignManifest Manifest, RedTeamFindings? Findings)> _redTeamCampaigns = new();
    private readonly List<RunPointer> _recentRuns = new();

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string? WorkspaceRoot => null;
    public bool IsAvailable => true;

    /// <summary>Pre-initializes the solution with a specific name (optional).</summary>
    public void Initialize(string name)
    {
        lock (_lock) _solution = new SolutionInfo(Guid.NewGuid(), name, "memory://");
    }

    public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_solution);
    }

    public Task<SubjectInfo> EnsureSubjectAsync(SubjectIdentity identity, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var key = (identity.Kind, identity.Name);
            if (!_subjects.TryGetValue(key, out var info))
            {
                info = new SubjectInfo(identity, null);
                _subjects[key] = info;
            }
            return Task.FromResult(info);
        }
    }

    public async IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(
        SubjectKind? kind = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<SubjectInfo> snapshot;
        lock (_lock)
            snapshot = kind.HasValue
                ? _subjects.Where(kv => kv.Key.Kind == kind.Value).Select(kv => kv.Value).ToList()
                : _subjects.Values.ToList();

        foreach (var s in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return s;
        }
        await Task.CompletedTask;
    }

    public Task<RunManifest> StartRunAsync(SubjectIdentity subject, RunContext context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var subjectKey = (subject.Kind, subject.Name);
            if (!_subjects.ContainsKey(subjectKey))
                _subjects[subjectKey] = new SubjectInfo(subject, null);

            var runId = GenerateRunId();
            var manifest = new RunManifest(
                SchemaVersion: "1.0",
                Solution: new SolutionRef(_solution.Id, _solution.Name, null),
                Subject: new SubjectRef(subject.Kind, subject.Name, subject.Version, subject.Framework, subject.ModelId, subject.SourceProject, subject.SourcePath),
                Run: new RunRef(runId, DateTimeOffset.UtcNow, TimeSpan.Zero, 0, "PENDING", context.Kind, context.EvalProject, context.EvalProjectPath, context.Harness, context.Seed, context.ParentInvocationId),
                Git: new GitRef(null, null, false, null),
                AgentEval: new AgentEvalRef("0.0.0", null),
                Environment: new EnvRef(Environment.MachineName, string.Empty, string.Empty, false, null, null),
                ContentHash: "sha256:0000000000000000000000000000000000000000000000000000000000000000");

            _runs[runId] = new RunEntry(subject, manifest, null, new Dictionary<string, ScenarioResult>(), null);
            return Task.FromResult(manifest);
        }
    }

    public Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(runId, out var entry))
                entry.Scenarios[result.Id] = result;
        }
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var runId = manifest.Run.RunId;
            var subject = manifest.Subject.ToIdentity();

            var hash = ComputeInMemoryHash(runId, summary);
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

            if (_runs.TryGetValue(runId, out var entry))
                _runs[runId] = entry with { Manifest = updated, Summary = summary };
            else
                _runs[runId] = new RunEntry(subject, updated, summary, new Dictionary<string, ScenarioResult>(), null);

            var subjectKey = (subject.Kind, subject.Name);
            _subjects[subjectKey] = new SubjectInfo(subject, summary);

            var historyKey = (subject.Kind, subject.Name);
            if (!_history.TryGetValue(historyKey, out var list))
            {
                list = new List<HistoryEntry>();
                _history[historyKey] = list;
            }
            list.Add(HistoryEntry.From(updated, summary));

            _recentRuns.Add(new RunPointer(runId, subject.Name, summary.Verdict, manifest.Run.Timestamp));
        }
        return Task.CompletedTask;
    }

    public Task AppendTraceAsync(string runId, AgentTrace trace, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(runId, out var entry))
                _runs[runId] = entry with { Trace = trace };
        }
        return Task.CompletedTask;
    }

    public Task SaveBaselineAsync(SubjectIdentity subject, RunSummary summary, string? versionTag = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (versionTag is not null)
                _pinnedBaselines[(subject.Kind, subject.Name, versionTag)] = summary;
            else
                _baselines[(subject.Kind, subject.Name)] = summary;
        }
        return Task.CompletedTask;
    }

    public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _baselines.TryGetValue((subject.Kind, subject.Name), out var summary);
            return Task.FromResult<RunSummary?>(summary);
        }
    }

    public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(runId, out var entry))
                throw new InvalidOperationException($"Run '{runId}' not found.");

            var current = entry.Summary
                ?? new RunSummary("1.0", runId, "PENDING", new RunStats(0, 0, 0, 0), new Dictionary<string, double>());

            _baselines.TryGetValue((entry.Subject.Kind, entry.Subject.Name), out var baseline);

            if (baseline is null)
                return Task.FromResult(new BaselineComparison(entry.Subject.Name, null, current, new Dictionary<string, double>(), false));

            var deltas = new Dictionary<string, double>();
            foreach (var key in current.Metrics.Keys.Intersect(baseline.Metrics.Keys))
                deltas[key] = current.Metrics[key] - baseline.Metrics[key];

            var anyDecreased = deltas.Values.Any(d => d < 0);
            var verdictWorsened = baseline.Verdict == "PASS" && current.Verdict != "PASS";
            var regressed = anyDecreased && verdictWorsened;

            return Task.FromResult(new BaselineComparison(entry.Subject.Name, baseline, current, deltas, regressed));
        }
    }

    public Task AppendHistoryEntryAsync(SubjectIdentity subject, HistoryEntry entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var key = (subject.Kind, subject.Name);
            if (!_history.TryGetValue(key, out var list))
            {
                list = new List<HistoryEntry>();
                _history[key] = list;
            }
            list.Add(entry);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<HistoryEntry> GetHistoryAsync(
        SubjectIdentity subject,
        DateRange? range = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<HistoryEntry> snapshot;
        lock (_lock)
        {
            var key = (subject.Kind, subject.Name);
            snapshot = _history.TryGetValue(key, out var list)
                ? list.ToList()
                : new List<HistoryEntry>();
        }

        foreach (var entry in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            if (range is not null && (entry.Timestamp < range.From || entry.Timestamp > range.To)) continue;
            yield return entry;
        }
        await Task.CompletedTask;
    }

    public Task SaveComplianceEvidenceAsync(string regulation, SubjectIdentity subject, ComplianceEvidence evidence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regulation);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(evidence);

        SchemaValidator.ValidateOrThrow(evidence, "evidence.schema.json");

        lock (_lock)
        {
            // Audit chain: source run must exist and its manifest hash must match.
            if (!_runs.TryGetValue(evidence.SourceRun.RunId, out var entry))
                throw new InvalidOperationException(
                    $"Cannot save evidence: source run {evidence.SourceRun.RunId} not found.");
            if (entry.Manifest.ContentHash != evidence.SourceRun.ManifestHash)
                throw new InvalidOperationException(
                    $"Manifest hash mismatch — source run has {entry.Manifest.ContentHash}, evidence references {evidence.SourceRun.ManifestHash}.");

            var ts = evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
            _complianceEvidence[(regulation, subject.Kind, subject.Name, ts)] = evidence;
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(
        string? regulation = null,
        SubjectIdentity? subject = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<((string Regulation, SubjectKind Kind, string SubjectName, string Ts) Key, ComplianceEvidence Evidence)> snapshot;
        lock (_lock)
            snapshot = _complianceEvidence.Select(kv => (kv.Key, kv.Value)).ToList();

        foreach (var (key, evidence) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            if (regulation is not null && key.Regulation != regulation) continue;
            if (subject is not null && (key.Kind != subject.Kind || key.SubjectName != subject.Name)) continue;
            yield return new ComplianceEvidencePointer(key.Regulation, key.SubjectName, key.Ts, evidence.Summary.OverallStatus);
        }
        await Task.CompletedTask;
    }

    public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _complianceEvidence.TryGetValue((regulation, subject.Kind, subject.Name, timestamp), out var evidence);
            return Task.FromResult<ComplianceEvidence?>(evidence);
        }
    }

    public Task<RedTeamCampaignManifest> StartRedTeamCampaignAsync(RedTeamCampaignContext context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var campaignId = $"{context.Name}_{ts}";
            var manifest = new RedTeamCampaignManifest(
                SchemaVersion: "1.0",
                CampaignId: campaignId,
                Name: context.Name,
                StartedAt: DateTimeOffset.UtcNow,
                Targets: context.Targets,
                Mode: context.Mode);
            _redTeamCampaigns[campaignId] = (manifest, null);
            return Task.FromResult(manifest);
        }
    }

    public Task CompleteRedTeamCampaignAsync(string campaignId, RedTeamFindings findings, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_redTeamCampaigns.TryGetValue(campaignId, out var campaign))
                throw new InvalidOperationException($"Campaign '{campaignId}' not found.");
            _redTeamCampaigns[campaignId] = (campaign.Manifest, findings);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RunPointer> GetRecentRunsAsync(
        int count = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<RunPointer> snapshot;
        lock (_lock) snapshot = _recentRuns.AsEnumerable().Reverse().Take(count).ToList();

        foreach (var pointer in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return pointer;
        }
        await Task.CompletedTask;
    }

    public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _runs.TryGetValue(runId, out var entry);
            return Task.FromResult<RunManifest?>(entry?.Manifest);
        }
    }

    public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _runs.TryGetValue(runId, out var entry);
            return Task.FromResult<RunSummary?>(entry?.Summary);
        }
    }

    public async IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<ScenarioResult> snapshot;
        lock (_lock)
        {
            snapshot = _runs.TryGetValue(runId, out var entry)
                ? entry.Scenarios.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList()
                : new List<ScenarioResult>();
        }

        foreach (var result in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return result;
        }
        await Task.CompletedTask;
    }

    public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _runs.TryGetValue(runId, out var entry);
            return Task.FromResult<AgentTrace?>(entry?.Trace);
        }
    }

    public async IAsyncEnumerable<RunManifest> ListRunsAsync(
        SubjectIdentity subject,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<RunManifest> snapshot;
        lock (_lock)
        {
            snapshot = _runs.Values
                .Where(e => e.Subject.Kind == subject.Kind && e.Subject.Name == subject.Name)
                .OrderBy(e => e.Manifest.Run.RunId, StringComparer.Ordinal)
                .Select(e => e.Manifest)
                .ToList();
        }

        foreach (var manifest in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return manifest;
        }
        await Task.CompletedTask;
    }

    private string ComputeInMemoryHash(string runId, RunSummary summary)
    {
        var json = JsonSerializer.Serialize(summary, s_jsonOpts);
        List<string> parts;
        lock (_lock)
        {
            parts = _runs.TryGetValue(runId, out var entry)
                ? entry.Scenarios.Keys.OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => JsonSerializer.Serialize(entry.Scenarios[k], s_jsonOpts))
                    .ToList()
                : new List<string>();
        }
        var combined = json + string.Concat(parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateRunId()
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{ts}_{hex}";
    }

    private sealed record RunEntry(
        SubjectIdentity Subject,
        RunManifest Manifest,
        RunSummary? Summary,
        Dictionary<string, ScenarioResult> Scenarios,
        AgentTrace? Trace);
}
