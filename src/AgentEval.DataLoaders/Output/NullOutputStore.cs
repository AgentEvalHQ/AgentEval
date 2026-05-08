// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

namespace AgentEval.Output;

/// <summary>No-op implementation of <see cref="IOutputStore"/> that discards all data.</summary>
public sealed class NullOutputStore : IOutputStore
{
    public string? WorkspaceRoot => null;
    public bool IsAvailable => false;

    public Task<SolutionInfo> EnsureSolutionAsync(CancellationToken ct = default)
        => Task.FromResult(new SolutionInfo(Guid.Empty, "<null>", ""));

    public Task<SubjectInfo> EnsureSubjectAsync(SubjectIdentity identity, CancellationToken ct = default)
        => Task.FromResult(new SubjectInfo(identity, null));

    public async IAsyncEnumerable<SubjectInfo> ListSubjectsAsync(
        SubjectKind? kind = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<RunManifest> StartRunAsync(SubjectIdentity subject, RunContext context, CancellationToken ct = default)
        => Task.FromResult(NullManifest(subject, context));

    public Task WriteScenarioResultAsync(string runId, ScenarioResult result, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CompleteRunAsync(RunManifest manifest, RunSummary summary, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendTraceAsync(string runId, AgentTrace trace, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SaveBaselineAsync(SubjectIdentity subject, RunSummary summary, string? versionTag = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<RunSummary?> LoadBaselineAsync(SubjectIdentity subject, CancellationToken ct = default)
        => Task.FromResult<RunSummary?>(null);

    public Task<BaselineComparison> CompareToBaselineAsync(string runId, CancellationToken ct = default)
    {
        var defaultSummary = new RunSummary("1.0", runId, "PASS", new RunStats(0, 0, 0, 0), new Dictionary<string, double>());
        return Task.FromResult(new BaselineComparison("<null>", null, defaultSummary, new Dictionary<string, double>(), false));
    }

    public Task AppendHistoryEntryAsync(SubjectIdentity subject, HistoryEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;

    public async IAsyncEnumerable<HistoryEntry> GetHistoryAsync(
        SubjectIdentity subject,
        DateRange? range = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task SaveComplianceEvidenceAsync(string regulation, SubjectIdentity subject, ComplianceEvidence evidence, CancellationToken ct = default)
        => Task.CompletedTask;

    public async IAsyncEnumerable<ComplianceEvidencePointer> ListComplianceEvidenceAsync(
        string? regulation = null,
        SubjectIdentity? subject = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<ComplianceEvidence?> GetComplianceEvidenceAsync(string regulation, SubjectIdentity subject, string timestamp, CancellationToken ct = default)
        => Task.FromResult<ComplianceEvidence?>(null);

    public Task<RedTeamCampaignManifest> StartRedTeamCampaignAsync(RedTeamCampaignContext context, CancellationToken ct = default)
        => Task.FromResult(NullCampaignManifest(context));

    public Task CompleteRedTeamCampaignAsync(string campaignId, RedTeamFindings findings, CancellationToken ct = default)
        => Task.CompletedTask;

    public async IAsyncEnumerable<RunPointer> GetRecentRunsAsync(
        int count = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<RunManifest?> GetRunManifestAsync(string runId, CancellationToken ct = default)
        => Task.FromResult<RunManifest?>(null);

    public Task<RunSummary?> GetRunSummaryAsync(string runId, CancellationToken ct = default)
        => Task.FromResult<RunSummary?>(null);

    public async IAsyncEnumerable<ScenarioResult> GetScenarioResultsAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<AgentTrace?> GetTraceAsync(string runId, CancellationToken ct = default)
        => Task.FromResult<AgentTrace?>(null);

    public async IAsyncEnumerable<RunManifest> ListRunsAsync(
        SubjectIdentity subject,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static RunManifest NullManifest(SubjectIdentity subject, RunContext context)
    {
        var runId = GenerateRunId();
        return new RunManifest(
            SchemaVersion: "1.0",
            Solution: new SolutionRef(Guid.Empty, "<null>", null),
            Subject: new SubjectRef(subject.Kind, subject.Name, subject.Version, subject.Framework, subject.ModelId, subject.SourceProject, subject.SourcePath),
            Run: new RunRef(runId, DateTimeOffset.UtcNow, TimeSpan.Zero, 0, "PENDING", context.Kind, context.EvalProject, context.EvalProjectPath, context.Harness, context.Seed, context.ParentInvocationId),
            Git: new GitRef(null, null, false, null),
            AgentEval: new AgentEvalRef("0.0.0", null),
            Environment: new EnvRef(Environment.MachineName, string.Empty, string.Empty, false, null, null),
            ContentHash: "sha256:0000000000000000000000000000000000000000000000000000000000000000");
    }

    private static RedTeamCampaignManifest NullCampaignManifest(RedTeamCampaignContext context)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        return new RedTeamCampaignManifest(
            SchemaVersion: "1.0",
            CampaignId: $"{context.Name}_{ts}",
            Name: context.Name,
            StartedAt: DateTimeOffset.UtcNow,
            Targets: context.Targets,
            Mode: context.Mode);
    }

    private static string GenerateRunId()
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{ts}_{hex}";
    }
}
