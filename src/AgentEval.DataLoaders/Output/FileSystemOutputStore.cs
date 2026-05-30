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

    // Per-runId resolution cache. Keyed by runId so concurrent GraphQL queries
    // for different runs do not tear a single shared field. ConcurrentDictionary
    // also gives us a memory ceiling-free read path; entries are added on first
    // resolve and never invalidated (run identity is immutable post-completion).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SubjectIdentity> _runSubjectCache = new(StringComparer.Ordinal);

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
#if NET9_0_OR_GREATER
        // Pin newline to LF so ContentHasher produces the same bytes regardless of
        // the host OS — otherwise Windows producers emit CRLF and Linux producers
        // emit LF, and the audit-chain hash differs across CI matrices. .NET 8
        // doesn't expose this option; on net8 we rely on `.gitattributes` to keep
        // committed JSON LF-only for any cross-platform consumer.
        _json.NewLine = "\n";
        _jsonl.NewLine = "\n";
#endif

        // Phase-0 security 0.9 (2026-05-13): stale-sentinel sweep was previously
        // invoked from this constructor unconditionally. That violated the
        // "read-only viewer" contract of Mission Control (plan-07 §1 +
        // docs/missioncontrol/getting-started.md), which constructs this store
        // at startup but must never mutate the workspace. Sweep is now an
        // explicit public method (`SweepStaleSentinelsAsync`) that ONLY the
        // CLI writer entry points (bench / init / migrate) call.
    }

    /// <summary>
    /// Best-effort sweep of stale sentinels left behind by killed processes:
    /// <c>*.invalid.json</c> siblings written when a schema-validate-before-write
    /// failed, and <c>*.lock</c> / <c>*.tmp</c> sentinels orphaned mid-write.
    /// Files older than <paramref name="olderThan"/> are deleted; IO errors
    /// (held files, permission denials) are swallowed by design — workspace
    /// hygiene must never block a benchmark run.
    /// </summary>
    /// <remarks>
    /// Callers: the three CLI bench writer commands (`bench gdpr` /
    /// `bench eu-ai-act` / `bench agentic`) call sweep after constructing the
    /// store, since they are the entry points that legitimately create the
    /// `*.lock` / `*.tmp` sentinels in the first place. `agenteval init` and
    /// `agenteval migrate` do not call this — they don't currently construct
    /// <see cref="FileSystemOutputStore"/> directly. Read-only consumers
    /// (Mission Control) MUST NOT call this — the method exists outside the
    /// ctor specifically to keep the read-only-viewer contract intact
    /// (Phase-0 security 0.9).
    /// </remarks>
    public Task SweepStaleSentinelsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        try { TrySweepStaleSentinels(_layout.Root, olderThan); }
        catch { /* intentional swallow — see remarks */ }
        return Task.CompletedTask;
    }

    private static void TrySweepStaleSentinels(string root, TimeSpan olderThan)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        foreach (var pattern in new[] { "*.invalid.json", "*.lock", "*.tmp" })
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff) info.Delete();
                }
                catch { /* skip files we can't access — another process may hold them */ }
            }
        }
    }

    public string? WorkspaceRoot => _layout.Root;
    public bool IsAvailable => File.Exists(_layout.SolutionFile);

    /// <inheritdoc/>
    /// <remarks>
    /// Plan-13 T4.1b item 11. Delegates to <see cref="FileSystemLayout.RunDir"/>
    /// — pure path projection, no I/O. The returned path includes the layout's
    /// <c>Sanitize</c> rewrites for Windows-invalid subject-name characters
    /// (<c>:</c>, <c>/</c>, <c>\</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, <c>|</c>, <c>?</c>, <c>*</c>),
    /// so callers no longer need to reach for <see cref="FileSystemLayout"/> directly.
    /// </remarks>
    public string ResolveRunDirectory(SubjectIdentity subject, string runId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _layout.RunDir(subject, runId);
    }

    /// <summary>
    /// Returns the canonical traces directory for a given subject and run, creating it if absent.
    /// </summary>
    /// <param name="subject">The subject identity.</param>
    /// <param name="runId">The run identifier.</param>
    /// <returns>The absolute path to the traces directory.</returns>
    public string GetTracesDirectory(SubjectIdentity subject, string runId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var path = _layout.TracesDir(subject, runId);
        Directory.CreateDirectory(path);
        return path;
    }

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

        // Concurrency gate — two parallel EnsureSubjectAsync calls (e.g. one
        // per-test fixture initialising a workspace, or two `agenteval bench`
        // runs racing) must not both pass the case-collision check below and
        // each write a subject.json. Take an exclusive file lock for the
        // read-check-write triple. The .lock sentinel is gitignored (see
        // .gitattributes + the gitignore entry added in task 3.7b).
        var lockPath = subjectFile + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        SubjectFileV1? existing;
        // Acquire an exclusive file lock with retry-on-contention. `FileShare.None`
        // makes the first opener exclusive; subsequent openers throw IOException.
        // Spin-wait with brief backoff until the holder releases (typical contention
        // window is < 1 ms — subject.json writes are microseconds). 30s overall
        // deadline; cancellation honoured during sleep.
        var lockDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        FileStream? lockHandle = null;
        while (lockHandle is null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lockHandle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < lockDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            }
        }
        await using (lockHandle)
        {
            existing = null;
            if (File.Exists(subjectFile))
            {
                try
                {
                    existing = await ReadJsonAsync<SubjectFileV1>(subjectFile, ct);
                }
                catch (JsonException jx)
                {
                    throw new InvalidOperationException(
                        $"subject.json at '{subjectFile}' is corrupt and cannot be deserialised " +
                        $"({jx.Message}). Manually inspect or delete the file to proceed; the " +
                        "case-collision and idempotency checks cannot run safely against unreadable JSON.",
                        jx);
                }
            }

            // Defend against case-insensitive filesystem collisions when subject.json
            // is intact and disagrees with the requested identity. On NTFS / default
            // APFS, "TravelAgent" and "travelagent" resolve to the same directory.
            if (existing is not null && !string.Equals(existing.Name, identity.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Subject directory '{Path.GetFileName(_layout.SubjectDir(identity))}' is already in use by subject " +
                    $"'{existing.Name}' (case-insensitive filesystem collision with requested name '{identity.Name}'). " +
                    "Pick a unique name that does not collide on case.");
            }

            // Defend against partial-init: directory exists but subject.json is
            // missing (e.g. prior process killed mid-write). Detect a sibling
            // directory in the same parent whose name matches OUR sanitised path
            // case-insensitively but with a different case — that's a collision
            // where the colliding subject hasn't even been able to write its file.
            if (existing is null)
            {
                var kindDir = _layout.SubjectKindDir(identity.Kind);
                if (Directory.Exists(kindDir))
                {
                    var requestedDirName = Path.GetFileName(_layout.SubjectDir(identity));
                    foreach (var siblingPath in Directory.GetDirectories(kindDir))
                    {
                        var siblingName = Path.GetFileName(siblingPath);
                        if (siblingName is null) continue;
                        if (string.Equals(siblingName, requestedDirName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(siblingName, requestedDirName, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Partial subject directory '{siblingName}' already exists alongside the " +
                                $"requested '{requestedDirName}' (case-only difference on a case-insensitive " +
                                "filesystem). Pick a unique name that does not collide on case, or remove " +
                                "the stale directory before retrying.");
                        }
                    }
                }
            }

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

            await ValidateAndWriteJsonAsync(subjectFile, dto, "subject.schema.json", ct);
        }
        // The .lock sentinel stays on disk as a zero-byte file — cheaper than
        // racing a Delete against another holder. Sweep periodically via the
        // store-ctor cleanup (see task 3.7b).

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

        await ValidateAndWriteJsonAsync(_layout.ManifestFile(subject, runId), manifest, "manifest.schema.json", ct);
        return manifest;
    }

    public async Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default)
    {
        var subject = await LocateRunAsync(runId, ct);
        var path = _layout.ScenarioFile(subject, runId, result.Id);

        // T2.6 (v1.1): the wrapper shape is C#-typed but Output is opaque text. When the
        // bench writer emits structured JSON as Output (e.g., a serialized EvalResult tree
        // from EvalResultPersistence.ToScenarioResult), we LOG a warning if it doesn't
        // round-trip through eval-result.schema.json — but do NOT abort the write. The
        // production EvalResult serializer's shape is intentionally looser than the schema
        // (it carries internal fields the schema doesn't enumerate). T2.6's value is
        // surfacing genuinely-malformed JSON (a truncated write, an injected garbage
        // string) at write time; over-strict schema enforcement would regress legitimate
        // writers. The doctor read-path (T1.6) carries the strict gate for files that DO
        // need full schema coverage.
        //
        // Raw-text Output (a string like "answer is 42") is also valid — many code-eval
        // scenarios persist the agent's raw response without a tree. We detect "intended
        // to be structured" by checking whether the trimmed payload starts with `{` or `[`.
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            var trimmed = result.Output.AsSpan().TrimStart();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(result.Output);
                    if (node is null)
                    {
                        Console.Error.WriteLine(
                            $"warning: scenario '{result.Id}' Output looks JSON-shaped but parsed to null — persisting as-is.");
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Console.Error.WriteLine(
                        $"warning: scenario '{result.Id}' Output looks JSON-shaped (starts with '{{' or '[') but is malformed: {ex.Message}");
                }
            }
        }

        await WriteJsonAsync(path, result, ct);
    }

    public async Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default)
    {
        var subject = manifest.Subject.ToIdentity();
        var runId = manifest.Run.RunId;

        await ValidateAndWriteJsonAsync(_layout.SummaryFile(subject, runId), summary, "summary.schema.json", ct);

        // Hash domain now includes the manifest (with contentHash zeroed). We
        // MUST hash against the final manifest (Verdict/Duration/ScenarioCount
        // populated), NOT the PENDING placeholder still on disk — otherwise
        // Verify would read the final manifest later and compute a different
        // hash. Build the final manifest in-memory FIRST, then hash with that
        // as the override, then write it.
        var pendingForHash = manifest with
        {
            ContentHash = "",   // will be overwritten with the real hash post-compute
            Run = manifest.Run with
            {
                Verdict = summary.Verdict,
                Duration = DateTimeOffset.UtcNow - manifest.Run.Timestamp,
                ScenarioCount = summary.Stats.Total
            }
        };
        var hash = await ContentHasher.HashRunAsync(_layout, subject, runId, pendingForHash, ct);
        var updated = pendingForHash with { ContentHash = $"sha256:{hash}" };
        await ValidateAndWriteJsonAsync(_layout.ManifestFile(subject, runId), updated, "manifest.schema.json", ct);

        var entry = HistoryEntry.From(updated, summary);
        await AppendHistoryEntryAsync(subject, entry, ct);

        // Populate the additive v1.7 fields so callers (Mission Control's
        // recentRuns resolver) don't need an N+1 lookup against summary.json
        // for the score / duration / cost columns.
        var score = summary.Stats.Total > 0
            ? (double)summary.Stats.Passed / summary.Stats.Total
            : 0.0;
        var estimatedCost = summary.Cost?.EstimatedCost;
        await AppendJsonlLineAsync(_layout.RecentRunsFile,
            new RunPointer(
                RunId: runId,
                SubjectName: subject.Name,
                Verdict: summary.Verdict,
                Timestamp: manifest.Run.Timestamp,
                Kind: subject.Kind,
                Score: score,
                DurationMs: (long)updated.Run.Duration.TotalMilliseconds,
                EstimatedCost: estimatedCost), ct);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(regulation);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(evidence);

        // The `regulation` segment flows directly into Path.Combine via
        // FileSystemLayout.ComplianceDir. Reject anything that could escape
        // the workspace — defence-in-depth in case a caller forgot to
        // validate the value (Mission Control already does, but callers
        // outside the portal may not).
        if (!FileSystemLayout.IsSafePathSegment(regulation))
            throw new ArgumentException(
                $"Regulation '{regulation}' contains characters that would compromise the on-disk path layout.", nameof(regulation));

        SchemaValidator.ValidateOrThrow(evidence, "evidence.schema.json");

        var sourceManifest = await GetRunManifestAsync(evidence.SourceRun.RunId, ct);
        if (sourceManifest is null)
            throw new InvalidOperationException(
                $"Cannot save evidence: source run {evidence.SourceRun.RunId} not found.");
        if (sourceManifest.ContentHash != evidence.SourceRun.ManifestHash)
            throw new InvalidOperationException(
                $"Manifest hash mismatch — source run has {sourceManifest.ContentHash}, evidence references {evidence.SourceRun.ManifestHash}.");

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
                    FileSystemLayout.Sanitize(subject.Name), StringComparison.OrdinalIgnoreCase))
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
        var sanitizedName = FileSystemLayout.Sanitize(context.Name);
        var campaignId = $"{sanitizedName}_{ts}";

        var manifest = new RedTeamCampaignManifest(
            SchemaVersion: "1.0",
            CampaignId: campaignId,
            Name: context.Name,
            StartedAt: DateTimeOffset.UtcNow,
            Targets: context.Targets,
            Mode: context.Mode);

        await ValidateAndWriteJsonAsync(_layout.RedTeamManifestFile(sanitizedName, ts), manifest, "red-team-manifest.schema.json", ct);
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

        // Validate ts looks like a timestamp — strict yyyy-MM-dd_HH-mm-ss
        // pattern. Previous code only checked first-char-is-digit, which let
        // malformed suffixes through that could re-trigger path semantics
        // (e.g. an embedded separator).
        if (ts.Length != tsSuffixLen ||
            !System.Text.RegularExpressions.Regex.IsMatch(ts, @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$"))
            throw new ArgumentException($"Invalid campaignId format: '{campaignId}'.", nameof(campaignId));

        await WriteJsonAsync(_layout.RedTeamFindingsFile(nameWithUnderscore, ts), findings, ct);
    }

    // T3.6 (2026-05-25) — Read-side enumeration of red-team campaigns. The
    // on-disk shape is `.agenteval/red-team/{sanitizedName}_{ts}/manifest.json`;
    // we walk the red-team directory and emit one RedTeamCampaignManifest per
    // child folder that contains a manifest.json. Missing or corrupt manifests
    // skip silently with a stderr warning so a single bad campaign does not
    // hide the whole list. Used by the MC `redTeamCampaigns` GraphQL resolver
    // (T3.6) and the `/red-team` SPA page.
    public async IAsyncEnumerable<RedTeamCampaignManifest> ListRedTeamCampaignsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var redTeamRoot = Path.Combine(_layout.Root, "red-team");
        if (!Directory.Exists(redTeamRoot)) yield break;

        foreach (var campaignDir in Directory.EnumerateDirectories(redTeamRoot))
        {
            ct.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(campaignDir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            RedTeamCampaignManifest? manifest = null;
            try
            {
                manifest = await ReadJsonAsync<RedTeamCampaignManifest>(manifestPath, ct);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Console.Error.WriteLine(
                    $"⚠ red-team manifest at {manifestPath} could not be deserialised: {ex.Message}");
                continue;
            }
            if (manifest is not null) yield return manifest;
        }
    }

    // T3.6 (2026-05-25) — Single-campaign lookup. Returns null when the
    // campaign id violates path-safety rules (rejects `../`, embedded
    // separators) or when no manifest exists for that id.
    public async Task<RedTeamCampaignManifest?> GetRedTeamCampaignAsync(
        string campaignId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(campaignId)) return null;
        // Defense-in-depth path-safety: the campaignId becomes a folder name
        // under .agenteval/red-team/, so reject any segment that contains
        // path-separator characters or starts with `..`.
        if (!FileSystemLayout.IsSafePathSegment(campaignId)) return null;

        var manifestPath = Path.Combine(_layout.Root, "red-team", campaignId, "manifest.json");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            return await ReadJsonAsync<RedTeamCampaignManifest>(manifestPath, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine(
                $"⚠ red-team manifest at {manifestPath} could not be deserialised: {ex.Message}");
            return null;
        }
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
            catch (JsonException ex)
            {
                // Don't fail the whole stream on a single bad line, but
                // surface the corruption so it isn't invisible. Writes to
                // stderr because this is a recoverable-by-design path
                // (recent.jsonl is regenerated on every run).
                Console.Error.WriteLine(
                    $"⚠ recent.jsonl: skipping malformed line in {recentFile}: {ex.Message}");
            }
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

    /// <summary>
    /// Validate <paramref name="value"/> against the named v1 schema and write
    /// it via <see cref="WriteJsonAsync{T}"/>. On schema-validation failure,
    /// dump the offending DTO JSON to a sibling <c>{path}.invalid.json</c>
    /// sidecar so the failure can be debugged without re-running the workload.
    /// The 24h sweep in the store ctor cleans up stale sidecars.
    /// </summary>
    private async Task ValidateAndWriteJsonAsync<T>(string path, T value, string schemaResource, CancellationToken ct)
    {
        try
        {
            SchemaValidator.ValidateOrThrow(value, schemaResource);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Schema validation failed", StringComparison.Ordinal))
        {
            try
            {
                var invalidPath = path + ".invalid.json";
                Directory.CreateDirectory(Path.GetDirectoryName(invalidPath)!);
                var json = JsonSerializer.Serialize(value, _json);
                await File.WriteAllTextAsync(invalidPath, json, ct);
                throw new InvalidOperationException(
                    $"{ex.Message}\nOffending JSON written to {invalidPath} for inspection.", ex);
            }
            catch (Exception sidecarEx) when (sidecarEx is not InvalidOperationException || !ReferenceEquals(sidecarEx.InnerException, ex))
            {
                // Could not write the sidecar (disk full, permissions). Rethrow
                // the original validation error so the caller still gets the
                // primary signal; the secondary failure is suppressed.
                throw ex;
            }
        }
        await WriteJsonAsync(path, value, ct);
    }

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic-replace: write to a sibling .tmp file then File.Move with
        // overwrite. Prevents readers from seeing a truncated/partial JSON
        // if the process is killed mid-write.
        //
        // The .tmp filename includes a per-call GUID suffix so two concurrent
        // writers targeting the same path (e.g. two `agenteval bench` runs
        // racing on the same workspace) do not stomp on each other's .tmp.
        // The reader at File.Move ignores per-call .tmp variants.
        var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, value, _json, ct);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup: if anything between Create and Move blew up,
            // don't leave a stray .tmp behind. Suppress secondary errors so the
            // original exception bubbles up.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Append a line to a JSONL file with cross-process safety. Two parallel
    /// <c>agenteval bench</c> runs (separate OS processes) that both target the
    /// same workspace will not interleave bytes mid-line: each takes a named
    /// kernel <see cref="System.Threading.Mutex"/> keyed by the canonical
    /// absolute path's SHA-256, plus an in-process <see cref="SemaphoreSlim"/>
    /// to short-circuit when the contention is within one process.
    /// </summary>
    private async Task AppendJsonlLineAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var canonicalPath = Path.GetFullPath(path);

        // In-process gate first — cheaper than acquiring the kernel mutex.
        var sem = s_appendLocks.GetOrAdd(canonicalPath, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Cross-process gate keyed on a SHA-256 of the canonical path. Mutex
            // names on Windows are limited to 260 chars and case-sensitive;
            // hash-truncated names stay portable. `Global\` ensures the lock
            // crosses session boundaries (terminal services).
            //
            // CRITICAL: `Mutex.WaitOne` and `Mutex.ReleaseMutex` must run on
            // the SAME thread (thread-affinity). The entire acquire→write→
            // release sequence therefore runs inside one Task.Run so the
            // continuation doesn't drift to another thread between WaitOne
            // and ReleaseMutex.
            var line = JsonSerializer.Serialize(value, _jsonl) + "\n";
            var hashBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalPath));
            var mutexName = "Global\\AgentEval-jsonl-" + Convert.ToHexString(hashBytes, 0, 8);

            await Task.Run(() =>
            {
                using var crossProc = new System.Threading.Mutex(initiallyOwned: false, name: mutexName);
                bool owned = false;
                try
                {
                    try { owned = crossProc.WaitOne(); }
                    catch (System.Threading.AbandonedMutexException)
                    {
                        // A prior holder exited without releasing — we still
                        // own the mutex now. Carry on with the append.
                        owned = true;
                    }
                    File.AppendAllText(path, line);
                }
                finally
                {
                    if (owned) crossProc.ReleaseMutex();
                }
            }, ct);
        }
        finally { sem.Release(); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> s_appendLocks = new(StringComparer.Ordinal);

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
        // Defence-in-depth: reject runIds that are not safe single path segments before they are
        // combined into a filesystem path. Live callers (GraphQL/REST) already gate with
        // IsSafePathSegment, but the store core was not safe-by-construction — and asymmetric,
        // since regulation/campaignId ARE validated internally. This closes a path-traversal gap
        // that a future internal caller (or multi-tenant Mode B) could otherwise reintroduce (SEC-06).
        if (!FileSystemLayout.IsSafePathSegment(runId))
            return null;

        if (_runSubjectCache.TryGetValue(runId, out var cached))
            return cached;

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
                _runSubjectCache.TryAdd(runId, identity);
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

    // Private write-only helper (not yet exposed but included for completeness)
    private async Task WriteSolutionAsync(Guid id, string name, CancellationToken ct)
    {
        var dto = new SolutionFileV1("1.0", id, name);
        await ValidateAndWriteJsonAsync(_layout.SolutionFile, dto, "solution.schema.json", ct);
    }

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
