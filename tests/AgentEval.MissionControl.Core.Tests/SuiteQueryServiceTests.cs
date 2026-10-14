// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using AgentEval.Results;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentEval.MissionControl.Core.Tests;

public sealed class SuiteQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-suite-query-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private EvidenceIndexRoot Root => new("root", Path.Combine(_root, "evidence"), IndexedEvidenceKind.Aef);
    public SuiteQueryServiceTests() => Directory.CreateDirectory(Root.Directory);
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private SuiteQueryService Service(params EvidenceIndexRoot[] roots) => new(new SqliteEvidenceSourceLookup(Options),
        new SuiteIdentityReader(), new SuiteCompatibilityEvaluator(), roots);

    private string Copy(string name)
    {
        string? fixture = null;
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            var candidate = Path.Combine(path.FullName, "contracts/aef/1/interop/examples/otel-aef/run");
            if (Directory.Exists(candidate)) { fixture = candidate; break; }
        }
        Assert.NotNull(fixture); var target = Path.Combine(Root.Directory, name);
        foreach (var source in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(fixture, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
        }
        return target;
    }
    private string Import(SqliteEvidenceIndexWriter writer, string name) =>
        writer.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", name, Copy(name))).SourceId;

    [Fact]
    public void IdentityAndCompare_SealedFixtures_VerifySelectedHashesWithoutInventingCompatibility()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = Import(writer, "first"); var second = Import(writer, "second"); var service = Service(Root);
        var identity = service.Identity(first);
        Assert.Equal(QueryState.Available, identity.State); Assert.Equal("otel-import-0001", identity.Data!.Identity.RunId);
        Assert.Equal(64, identity.Data.Identity.RunHash.Length);
        var compared = service.Compare(first, second);
        Assert.Equal(QueryState.Available, compared.State);
        Assert.False(compared.Data!.Compatibility.Compatible);
        Assert.Equal("definition-required", compared.Data.Compatibility.Reason);
        Assert.Equal(compared.Data.Baseline.IndexGeneration, compared.Data.Candidate.IndexGeneration);
    }

    [Fact]
    public void Compare_CompletedAndAbortedFrozenDefinition_CompatibleWithoutClaimingCompleteness()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var baseline = WriteDefinition(writer, "complete", frozen: true, aborted: false);
        var candidate = WriteDefinition(writer, "aborted", frozen: true, aborted: true);
        var outcome = Service(Root).Compare(baseline, candidate);
        Assert.Equal(QueryState.Available, outcome.State);
        Assert.True(outcome.Data!.Compatibility.Compatible);
        Assert.Equal("same-definition", outcome.Data.Compatibility.Reason);
        Assert.Equal("completed", outcome.Data.Baseline.Identity.RunStatus);
        Assert.Equal("aborted", outcome.Data.Candidate.Identity.RunStatus);
        Assert.NotEqual(outcome.Data.Baseline.Identity.RunHash, outcome.Data.Candidate.Identity.RunHash);
    }

    [Theory]
    [InlineData(true, 0.9, "definition-mismatch")]
    [InlineData(false, 0.8, "generated-content")]
    public void Compare_ChangedDefinitionOrUnfrozenRecipe_RemainsIncompatible(bool frozen, double threshold, string reason)
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var baseline = WriteDefinition(writer, "baseline", frozen: true, aborted: false);
        var candidate = WriteDefinition(writer, "candidate", frozen, aborted: false, threshold);
        var outcome = Service(Root).Compare(baseline, candidate);
        Assert.Equal(QueryState.Available, outcome.State);
        Assert.False(outcome.Data!.Compatibility.Compatible); Assert.Equal(reason, outcome.Data.Compatibility.Reason);
    }

    private string WriteDefinition(SqliteEvidenceIndexWriter index, string name, bool frozen, bool aborted, double threshold = 0.8, string? padding = null)
    {
        var manifest = new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:query",
            ["definitionVersion"] = "1", ["frozen"] = frozen,
            ["inputs"] = new JsonObject { ["rubric"] = SuiteDefinitionManifest.Hash("fixture"u8) },
            ["definition"] = new JsonObject { ["threshold"] = threshold }, ["caseIds"] = new JsonArray("case-a")
        }.ToJsonString()));
        var directory = Path.Combine(Root.Directory, name);
        var started = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var run = AefRunWriter.Create(directory, new AefRunHeader
        {
            RunId = name, Producer = new() { Name = "query-fixture", Version = "1" },
            Subject = new() { Ref = "agent:query", Kind = AefSubjectKind.Agent, Version = "1" },
            StartedAt = started, ContentCapture = AefContentCapture.Off,
            Execution = new() { TargetMode = AefTargetMode.Live, Stimulus = frozen ? AefStimulus.Suite : AefStimulus.Generated },
            Suite = new() { Ref = manifest.SuiteRef, Version = manifest.DefinitionVersion, Frozen = frozen, Digest = manifest.SuiteContentDigest },
            Ext = new JsonObject { ["agenteval.suiteIdentity"] = manifest.Metadata() }
        });
        run.SetMetrics([new() { Id = "quality", Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) }]);
        run.PutExtFile(SuiteDefinitionManifest.ExtensionFile, manifest.ToBytes());
        if (!aborted) run.AddResult(new() { CaseId = "case-a", Path = "quality", Evaluator = new("code:quality"),
            State = AefState.Passed, Ext = padding is null ? null : new JsonObject { ["fixture.padding"] = padding }, Scores = [new() { Metric = "quality", Value = 0.9 }] });
        if (!aborted) run.SetSummary(new() { Lanes = [new AefSummaryLane("main", [new() { Metric = "quality", Path = "quality" }])] });
        run.Close(aborted ? AefRunStatus.Aborted : AefRunStatus.Completed, started.AddSeconds(1), aborted ? "cancelled" : null);
        AefSealer.Seal(directory);
        return index.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", name, directory)).SourceId;
    }

    [Fact]
    public void ResultDetail_VerifiedOverrideAndUnsealedTail_KeepSealedAndEffectiveStatesSeparate()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = Import(writer, "detail");
        var indexed = new SqliteEvidenceDetailReader(Options).ReadResults(new(id)).Items[0];
        var service = new ResultDetailQueryService(new SqliteEvidenceSourceLookup(Options), new AefResultDetailReader(), [Root]);
        var at = AefTime.Parse("2026-10-14T00:00:00Z");
        var before = service.Result(id, indexed.ResultId, at); Assert.Equal(QueryState.Available, before.State);
        var overlay = AefOverlayWriter.Open(Path.Combine(Root.Directory, "detail"));
        var changed = overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Override, Target = new() { Result = indexed.ResultId },
            By = new("reviewer:fixture", AefAssurance.Authenticated), At = at,
            State = AefState.Warn, Reason = "verified review"
        });
        overlay.SealBatch();
        overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Override, Target = new() { Result = indexed.ResultId },
            By = new("reviewer:fixture", AefAssurance.SelfAttested), At = at,
            State = AefState.Passed, Reason = "unfinished review"
        });
        var outcome = service.Result(id, indexed.ResultId, at);
        Assert.Equal(QueryState.Available, outcome.State); var detail = outcome.Data!.Result;
        Assert.Equal(before.Data!.Result.SealedJson, detail.SealedJson);
        Assert.Equal(indexed.Outcome, detail.SealedState); Assert.Equal("warn", detail.EffectiveState);
        Assert.Equal(changed, detail.OverrideEvent); Assert.Equal(1, detail.UnsealedEvents);
        Assert.Equal("self-attested", Assert.Single(detail.Assurance).Shown);
        Assert.NotEqual(before.Data.Result.SnapshotDigest, detail.SnapshotDigest);
        Assert.Equal(before.Data.Result.RunHash, detail.RunHash);
        Assert.Equal(QueryState.NotFound, service.Result(id, "missing-result", at).State);
        File.WriteAllText(Path.Combine(Root.Directory, "detail", "overlays", "seal-0001.json"), "{}");
        Assert.Equal(QueryState.Unavailable, service.Result(id, indexed.ResultId, at).State);
    }

    [Fact]
    public void ResultDetail_TamperedEvidenceInvalidTimeAndCaptureLimit_RefuseWithoutPartialDetail()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = Import(writer, "detail");
        var result = new SqliteEvidenceDetailReader(Options).ReadResults(new(id)).Items[0].ResultId;
        var at = AefTime.Parse("2026-10-14T00:00:00Z");
        var limited = new ResultDetailQueryService(new SqliteEvidenceSourceLookup(Options),
            new AefResultDetailReader(new AefStagingOptions { MaxBytes = 100, MaxFileBytes = 100 }), [Root]);
        Assert.Equal(QueryState.ResourceLimit, limited.Result(id, result, at).State);
        Assert.Null(limited.Result(id, result, at).Data);
        var service = new ResultDetailQueryService(new SqliteEvidenceSourceLookup(Options), new AefResultDetailReader(), [Root]);
        Assert.Equal(QueryState.InvalidRequest, service.Result(id, result, new AefTime(0, -1)).State);
        File.AppendAllText(Path.Combine(Root.Directory, "detail", "results.ndjson"), " ");
        Assert.Equal(QueryState.Unavailable, service.Result(id, result, at).State);
        Assert.Throws<OperationCanceledException>(() => service.Result(id, result, at, new(true)));
    }

    [Fact]
    public void ResultDetail_OversizedResponse_RefusesWithoutReturningPartialContent()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var id = WriteDefinition(writer, "large", true, false, padding: new string('x', 2 << 20));
        var resultId = new SqliteEvidenceDetailReader(Options).ReadResults(new(id)).Items[0].ResultId;
        var service = new ResultDetailQueryService(new SqliteEvidenceSourceLookup(Options), new AefResultDetailReader(), [Root]);
        var result = service.Result(id, resultId, AefTime.Parse("2026-10-14T00:00:00Z"));
        Assert.Equal(QueryState.ResourceLimit, result.State); Assert.Null(result.Data);
        Assert.Equal("detail-byte-limit", result.Code);
    }

    [Fact]
    public void Governance_ExplicitEvidenceAndEmptySelection_PreserveSubjectStatusAndProvenance()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = WriteDefinition(writer, "governance", true, false);
        var service = Governance(); var at = AefTime.Parse("2026-10-01T11:00:00Z");
        var result = service.Status(new("agent:query", "1"), [id, id], at);
        Assert.Equal(QueryState.Available, result.State); Assert.Single(result.Data!.SelectedRunHashes);
        Assert.Equal("met", Assert.Single(result.Data.Requirements).Status);
        Assert.Equal("not_measured", Assert.Single(service.Status(new("agent:query", "2"), [id], at).Data!.Requirements).Status);
        Assert.Equal("not_measured", Assert.Single(service.Status(new("agent:query", "1"), [], at).Data!.Requirements).Status);
        Assert.Equal(QueryState.ResourceLimit, service.Status(new("agent:query", "1"), Enumerable.Repeat(id, 9).ToArray(), at).State);
        Assert.Throws<OperationCanceledException>(() => service.Status(new("agent:query", "1"), [id], at, new(true)));
    }

    [Fact]
    public void Governance_SignedWaiver_RequiresScopedAuthorityAndActiveExpiry()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = WriteDefinition(writer, "waiver", true, false);
        using var signer = EcdsaP256Signer.Generate();
        var overlay = AefOverlayWriter.Open(Path.Combine(Root.Directory, "waiver"));
        overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Waive, Target = new() { Requirement = "team:quality" },
            By = new("reviewer:fixture", AefAssurance.Signed), At = AefTime.Parse("2026-10-01T10:30:00Z"),
            Expires = AefTime.Parse("2026-10-01T12:00:00Z"), Reason = "time-limited exception"
        });
        overlay.SealBatch(signer);
        var policy = new TrustPolicy([new TrustedKey("reviewer:fixture", signer.PublicKey, [RequirementEvidenceReader.WaivePermission("team:quality")])]);
        var query = Governance(0.95, policy);
        var active = query.Status(new("agent:query", "1"), [id], AefTime.Parse("2026-10-01T11:00:00Z"));
        Assert.Equal(QueryState.Available, active.State); var status = Assert.Single(active.Data!.Requirements);
        Assert.Equal("waived", status.Status); Assert.True(Assert.Single(status.Waivers).Authorized);
        Assert.Equal("signed", Assert.Single(status.Waivers).Assurance);
        var expired = query.Status(new("agent:query", "1"), [id], AefTime.Parse("2026-10-01T13:00:00Z"));
        Assert.NotEqual("waived", Assert.Single(expired.Data!.Requirements).Status);
        var noAuthority = new TrustPolicy([new TrustedKey("reviewer:fixture", signer.PublicKey)]);
        var unsignedAuthority = Governance(0.95, noAuthority).Status(new("agent:query", "1"), [id], AefTime.Parse("2026-10-01T11:00:00Z"));
        Assert.False(Assert.Single(Assert.Single(unsignedAuthority.Data!.Requirements).Waivers).Authorized);
        Assert.NotEqual("waived", Assert.Single(unsignedAuthority.Data.Requirements).Status);
    }

    private GovernanceQueryService Governance(double threshold = 0.8, TrustPolicy? policy = null,
        GovernanceQueryLimits? limits = null, IEvidenceSourceLookup? lookup = null)
    {
        var catalogue = new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["requirements"] = new JsonArray(new JsonObject
            {
                ["id"] = "team:quality", ["selector"] = new JsonObject { ["lane"] = "main", ["metric"] = "quality", ["path"] = "quality" },
                ["acceptance"] = new JsonObject { ["kind"] = "threshold", ["operator"] = ">=", ["value"] = threshold },
                ["aggregation"] = new JsonObject { ["kind"] = "all", ["missing"] = "not_measured" },
                ["freshnessSeconds"] = 86400, ["allowEffectiveOverrides"] = false
            })
        }.ToJsonString()));
        return new(lookup ?? new SqliteEvidenceSourceLookup(Options), new BoundedRequirementEvidenceReader(new RequirementEvidenceReader()),
            new RequirementStatusResolver(), catalogue, [Root], policy, limits);
    }

    [Fact]
    public void Governance_AggregateCaptureAndWorkBudgets_ApplyAcrossSelectedRuns()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = WriteDefinition(writer, "first-budget", true, false);
        var second = WriteDefinition(writer, "second-budget", true, false);
        long Bytes(string name) => Directory.EnumerateFiles(Path.Combine(Root.Directory, name), "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        var total = Bytes("first-budget") + Bytes("second-budget");
        var subject = new RequirementSubject("agent:query", "1"); var at = AefTime.Parse("2026-10-01T11:00:00Z");
        Assert.Equal(QueryState.Available, Governance(limits: new() { MaxCaptureBytes = total, MaxRowRulePairs = 2 }).Status(subject, [first, second], at).State);
        var bytes = Governance(limits: new() { MaxCaptureBytes = total - 1 }).Status(subject, [first, second], at);
        Assert.Equal(QueryState.ResourceLimit, bytes.State); Assert.Equal("evidence-byte-limit", bytes.Code); Assert.Null(bytes.Data);
        var work = Governance(limits: new() { MaxRowRulePairs = 1 }).Status(subject, [first, second], at);
        Assert.Equal(QueryState.ResourceLimit, work.State); Assert.Equal("governance-work-limit", work.Code); Assert.Null(work.Data);
    }

    [Fact]
    public void Governance_ResponseBudget_UsesExactSerializedBytesAndReturnsNoPartialReport()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = WriteDefinition(writer, "response-budget", true, false);
        var subject = new RequirementSubject("agent:query", "1"); var at = AefTime.Parse("2026-10-01T11:00:00Z");
        var expected = Governance().Status(subject, [id], at).Data!;
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(expected).Length;
        Assert.Equal(QueryState.Available, Governance(limits: new() { MaxResponseBytes = bytes }).Status(subject, [id], at).State);
        var limited = Governance(limits: new() { MaxResponseBytes = bytes - 1 }).Status(subject, [id], at);
        Assert.Equal(QueryState.ResourceLimit, limited.State); Assert.Equal("governance-response-limit", limited.Code); Assert.Null(limited.Data);
    }

    [Fact]
    public void Governance_ChangedIndexSelection_RefusesMixedRevisionReport()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = WriteDefinition(writer, "first-revision", true, false); var second = WriteDefinition(writer, "second-revision", true, false);
        var service = Governance(lookup: new ChangingLookup(new SqliteEvidenceSourceLookup(Options)));
        var result = service.Status(new("agent:query", "1"), [first, second], AefTime.Parse("2026-10-01T11:00:00Z"));
        Assert.Equal(QueryState.StaleCursor, result.State); Assert.Equal("selection-changed", result.Code); Assert.Null(result.Data);
    }

    [Fact]
    public void Governance_SameSealWithConflictingOverlaySnapshots_RefusesInsteadOfChoosingAnAlias()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = WriteDefinition(writer, "original", true, false);
        var original = Path.Combine(Root.Directory, "original"); var alias = Path.Combine(Root.Directory, "alias");
        foreach (var file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(alias, Path.GetRelativePath(original, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        var second = writer.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", "alias", alias)).SourceId;
        var service = Governance(); var at = AefTime.Parse("2026-10-01T11:00:00Z");
        Assert.Single(service.Status(new("agent:query", "1"), [first, second], at).Data!.SelectedRunHashes);
        var resultId = new SqliteEvidenceDetailReader(Options).ReadResults(new(second)).Items[0].ResultId;
        var overlay = AefOverlayWriter.Open(alias);
        overlay.Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Override, Target = new() { Result = resultId }, State = AefState.Failed,
            By = new("reviewer:fixture", AefAssurance.SelfAttested), At = at, Reason = "different overlay history"
        });
        overlay.SealBatch();
        var conflict = service.Status(new("agent:query", "1"), [first, second], at);
        Assert.Equal(QueryState.Unavailable, conflict.State); Assert.Equal("invalid-governance-evidence", conflict.Code); Assert.Null(conflict.Data);
    }

    [Fact]
    public void Governance_HostLimitsOutsideCeilings_RefuseConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Governance(limits: new() { MaxSources = 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Governance(limits: new() { MaxCaptureBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Governance(limits: new() { MaxRowRulePairs = 100_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Governance(limits: new() { MaxResponseBytes = (2 << 20) + 1 }));
    }

    [Fact]
    public void Identity_UnconfiguredOrUnboundOrReboundRoot_DoesNotReadEvidence()
    {
        using var writer = Writer(); var id = Import(writer, "first");
        Assert.Equal(QueryState.NotFound, Service().Identity(id).State);
        Assert.Equal("root-binding-mismatch", Service(Root).Identity(id).Code);
        // A separate index with a persisted root cannot be redirected by a different runtime configuration.
        var options = new EvidenceIndexOptions { DatabasePath = Path.Combine(_root, "bound.sqlite") };
        using var boundWriter = new SqliteEvidenceIndexWriter(options, new SqliteEvidenceIndexSchema());
        boundWriter.BindRoots([Root]);
        var boundId = boundWriter.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", "first", Path.Combine(Root.Directory, "first"))).SourceId;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var service = new SuiteQueryService(new SqliteEvidenceSourceLookup(options), new SuiteIdentityReader(),
            new SuiteCompatibilityEvaluator(), [Root with { Directory = other }]);
        Assert.Equal("root-binding-mismatch", service.Identity(boundId).Code);
    }

    [Fact]
    public void Identity_SourceChangesAfterIndexing_RefusesWithoutReturningOldIdentity()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = Import(writer, "first");
        File.AppendAllText(Path.Combine(Root.Directory, "first", "run.json"), " ");
        var result = Service(Root).Identity(id);
        Assert.Equal(QueryState.Unavailable, result.State); Assert.Null(result.Data); Assert.Equal("invalid-sealed-evidence", result.Code);
    }

    [Fact]
    public void Compare_IndexRevisionChangesBetweenSelections_RequiresReselection()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var first = Import(writer, "first"); var second = Import(writer, "second");
        var lookup = new ChangingLookup(new SqliteEvidenceSourceLookup(Options));
        var service = new SuiteQueryService(lookup, new SuiteIdentityReader(), new SuiteCompatibilityEvaluator(), [Root]);
        var result = service.Compare(first, second);
        Assert.Equal(QueryState.StaleCursor, result.State); Assert.Equal("selection-changed", result.Code); Assert.Null(result.Data);
    }

    [Fact]
    public void Identity_InvalidMissingOrCancelledRequest_RemainsExplicit()
    {
        using var writer = Writer(); var service = Service(Root);
        Assert.Equal(QueryState.InvalidRequest, service.Identity("../run").State);
        Assert.Equal(QueryState.NotFound, service.Identity(new string('0', 64)).State);
        Assert.Throws<OperationCanceledException>(() => service.Identity(new string('0', 64), new(true)));
    }

    private sealed class ChangingLookup(IEvidenceSourceLookup inner) : IEvidenceSourceLookup
    {
        private int _count;
        public EvidenceSourceSelection? Find(string sourceId, CancellationToken cancellationToken = default)
        {
            var selected = inner.Find(sourceId, cancellationToken);
            return selected is null ? null : selected with { Generation = selected.Generation + _count++ };
        }
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
