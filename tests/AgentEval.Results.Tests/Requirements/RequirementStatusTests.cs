using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Writing;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Requirements;

public class RequirementStatusTests
{
    private static readonly AefTime Now = AefTime.Parse("2026-10-01T11:00:00Z");
    private static readonly AefTime Expiry = AefTime.Parse("2026-10-01T12:00:00Z");

    private static RequirementCatalogue Catalogue(string aggregation = "all", bool state = false, bool overrides = false,
        string path = "q", string? suite = null) => new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(
        new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["requirements"] = new JsonArray(new JsonObject
            {
                ["id"] = "team:quality",
                ["selector"] = suite is null
                    ? new JsonObject { ["lane"] = "main", ["metric"] = "m", ["path"] = path }
                    : new JsonObject { ["lane"] = "main", ["metric"] = "m", ["path"] = path, ["suiteRef"] = suite },
                ["acceptance"] = state ? new JsonObject { ["kind"] = "state", ["states"] = new JsonArray("passed") }
                    : new JsonObject { ["kind"] = "threshold", ["operator"] = ">=", ["value"] = 0.8 },
                ["aggregation"] = aggregation == "fraction" ? new JsonObject { ["kind"] = aggregation, ["minimum"] = 0.5, ["missing"] = "not_measured" }
                    : new JsonObject { ["kind"] = aggregation, ["missing"] = "not_measured" },
                ["freshnessSeconds"] = 86400, ["allowEffectiveOverrides"] = overrides,
            }),
        }.ToJsonString()));

    private static WriterRun Run(out string hash, bool missing = false)
    {
        var run = new WriterRun();
        if (missing)
        {
            var writer = run.Create();
            writer.AddResult(WriterRun.Leaf("k1", state: AefState.NotMeasured, m: null));
            writer.AddResult(WriterRun.Leaf("k2"));
            writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
            writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(5));
        }
        else run.WriteSmall();
        hash = AefSealer.Seal(run.Dir, WriterRun.Options()).RunHash;
        return run;
    }

    private static RequirementStatus Resolve(RequirementCatalogue catalogue, WriterRun run, string hash,
        AefTime? at = null, TrustPolicy? policy = null, string version = "1")
    {
        var time = at ?? Now;
        var evidence = new RequirementEvidenceReader().Read(new(run.Dir, hash), time, policy);
        return Assert.Single(new RequirementStatusResolver().Resolve(catalogue, new("agent:t", version),
            [evidence], time, RequirementEvidenceReader.PolicyDigest(policy)).Requirements);
    }

    private static void Decision(WriterRun run, AefOverlayKind kind, AefOverlayTarget target,
        IAefSigner? signer = null, AefState? state = null, bool seal = true)
    {
        var writer = AefOverlayWriter.Open(run.Dir);
        writer.Append(new AefOverlayEvent { Kind = kind, Target = target, State = state, Reason = "reviewed evidence",
            By = new(WriterRun.Alice, AefAssurance.SelfAttested), At = WriterRun.Start.AddMinutes(30),
            Expires = kind == AefOverlayKind.Waive ? Expiry : null });
        if (seal) writer.SealBatch(signer);
    }

    [Theory]
    [InlineData("all", "not_met")]
    [InlineData("any", "met")]
    [InlineData("fraction", "met")]
    public void Resolve_MixedMeasuredEvidence_UsesExplicitAggregation(string aggregation, string expected)
    {
        using var run = Run(out var hash);
        var before = run.Snapshot();
        var result = Resolve(Catalogue(aggregation), run, hash);
        Assert.Equal(expected, result.BaseStatus);
        Assert.Equal(expected, result.Status);
        Assert.Equal(2, result.Observations.Count);
        Assert.Contains(result.Observations, o => o.Value == 0.4 && o.SealedState == "failed");
        Assert.All(before, f => Assert.Equal(f.Value, run.Read(f.Key)));
    }

    [Fact]
    public void Resolve_NoEvidence_ReturnsNotMeasuredWithReproducibilityInputs()
    {
        var catalogue = Catalogue();
        var report = new RequirementStatusResolver().Resolve(catalogue, new("agent:t", "1"), [], Now, RequirementEvidenceReader.PolicyDigest(null));
        Assert.Equal(catalogue.Digest, report.CatalogueDigest);
        Assert.Equal(Now.ToString(), report.At);
        Assert.Empty(report.SelectedRunHashes);
        Assert.Equal("not_measured", Assert.Single(report.Requirements).Status);
    }

    [Fact]
    public void Resolve_WrongVersionOrMissingSelector_ExplainsNotMeasured()
    {
        using var run = Run(out var hash);
        var wrong = Resolve(Catalogue(), run, hash, version: "2");
        Assert.Equal("not_measured", wrong.Status);
        Assert.Contains(wrong.Diagnostics, d => d.Contains("subject.version mismatch"));
        Assert.Equal("not_measured", Resolve(Catalogue(path: "other"), run, hash).Status);
        Assert.Equal("not_measured", Resolve(Catalogue(suite: "suite:other"), run, hash).Status);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("any")]
    [InlineData("fraction")]
    public void Resolve_MissingValueWithPassingObservation_NeverTreatsAbsenceAsSuccess(string aggregation)
    {
        using var run = Run(out var hash, missing: true);
        Assert.Equal("not_measured", Resolve(Catalogue(aggregation), run, hash).Status);
    }

    [Fact]
    public void Resolve_FreshnessBoundary_PreservesNanosecondPrecision()
    {
        using var run = Run(out var hash);
        Assert.Equal("not_met", Resolve(Catalogue(), run, hash, AefTime.Parse("2026-10-02T10:05:00Z")).Status);
        Assert.Equal("stale", Resolve(Catalogue(), run, hash, AefTime.Parse("2026-10-02T10:05:00.000000001Z")).Status);
        Assert.Equal("not_measured", Resolve(Catalogue(), run, hash, AefTime.Parse("2026-10-01T10:04:59Z")).Status);
    }

    [Fact]
    public void Read_WrongHashTamperedOrUnsealed_RefusesInsteadOfReportingAStatus()
    {
        using var run = Run(out var hash);
        var reader = new RequirementEvidenceReader();
        Assert.Throws<RequirementStatusException>(() => reader.Read(new(run.Dir, new string('a', 64)), Now));
        File.AppendAllText(run.Full("results.ndjson"), "{}\n");
        Assert.Throws<RequirementStatusException>(() => reader.Read(new(run.Dir, hash), Now));
        using var unsealed = new WriterRun();
        unsealed.WriteSmall();
        Assert.Throws<RequirementStatusException>(() => reader.Read(new(unsealed.Dir, hash), Now));
    }

    [Fact]
    public void Resolve_ApprovedReview_DoesNotChangeFailedMeasurement()
    {
        using var run = Run(out var hash);
        Decision(run, AefOverlayKind.Approve, new());
        var result = Resolve(Catalogue(), run, hash);
        Assert.Equal("not_met", result.Status);
        Assert.Equal("approve", Assert.Single(result.Reviews).Status);
    }

    [Fact]
    public void Resolve_OverrideRequiresOptIn_AlwaysRetainsSealedBaseAndNumericValues()
    {
        using var run = Run(out var hash);
        var id = run.Lines("results.ndjson")[0]["resultId"]!.GetValue<string>();
        Decision(run, AefOverlayKind.Override, new() { Result = id }, state: AefState.Passed);
        Assert.Equal("not_met", Resolve(Catalogue(state: true), run, hash).Status);
        var optedIn = Resolve(Catalogue(state: true, overrides: true), run, hash);
        Assert.Equal("not_met", optedIn.BaseStatus);
        Assert.Equal("met", optedIn.EffectiveStatus);
        Assert.NotNull(optedIn.Observations[0].OverrideEvent);
        Assert.Equal(0.4, optedIn.Observations[0].Value);
        Assert.Equal("not_met", Resolve(Catalogue(), run, hash).Status);
    }

    [Theory]
    [InlineData(false, false, "not_met")]
    [InlineData(true, false, "not_met")]
    [InlineData(false, true, "not_met")]
    [InlineData(true, true, "waived")]
    public void Resolve_Waiver_NeedsSignatureAndScopedAuthority(bool signed, bool grant, string expected)
    {
        using var run = Run(out var hash);
        using var signer = EcdsaP256Signer.Generate();
        Decision(run, AefOverlayKind.Waive, new() { Requirement = "team:quality" }, signed ? signer : null);
        var policy = new TrustPolicy([new TrustedKey(WriterRun.Alice, signer.PublicKey,
            grant ? [RequirementEvidenceReader.WaivePermission("team:quality")] : null)]);
        var result = Resolve(Catalogue(), run, hash, policy: policy);
        Assert.Equal(expected, result.Status);
        Assert.Equal("not_met", result.BaseStatus);
        Assert.Equal(signed ? "signed" : "self-attested", Assert.Single(result.Waivers).Assurance);
        Assert.Equal("not_met", Resolve(Catalogue(), run, hash, at: Expiry, policy: policy).Status);
        Assert.False(Assert.Single(Resolve(Catalogue(), run, hash, at: Expiry, policy: policy).Waivers).Active);
    }

    [Fact]
    public void Resolve_ResultAndDifferentNamespaceWaivers_DoNotPropagate()
    {
        using var run = Run(out var hash);
        using var signer = EcdsaP256Signer.Generate();
        Decision(run, AefOverlayKind.Waive, new() { Requirement = "other:quality" }, signer);
        Decision(run, AefOverlayKind.Waive, new() { Result = run.Lines("results.ndjson")[0]["resultId"]!.GetValue<string>() }, signer);
        var policy = new TrustPolicy([new TrustedKey(WriterRun.Alice, signer.PublicKey, [RequirementEvidenceReader.WaivePermission("team:quality")])]);
        var result = Resolve(Catalogue(), run, hash, policy: policy);
        Assert.Equal("not_met", result.Status);
        Assert.Empty(result.Waivers);
    }

    [Fact]
    public void Resolve_UnsealedOverride_IsIgnoredAndReported()
    {
        using var run = Run(out var hash);
        Decision(run, AefOverlayKind.Override, new() { Result = run.Lines("results.ndjson")[0]["resultId"]!.GetValue<string>() }, state: AefState.Passed, seal: false);
        var result = Resolve(Catalogue(state: true, overrides: true), run, hash);
        Assert.Equal("not_met", result.Status);
        Assert.Contains(result.Diagnostics, d => d.Contains("unsealed overlay"));
    }

    [Fact]
    public void Resolve_DuplicateSelection_IsOneRunAndMismatchedContextRefuses()
    {
        using var run = Run(out var hash);
        var evidence = new RequirementEvidenceReader().Read(new(run.Dir, hash), Now);
        var resolver = new RequirementStatusResolver();
        var report = resolver.Resolve(Catalogue(), new("agent:t", "1"), [evidence, evidence], Now, evidence.PolicyDigest);
        Assert.Single(report.SelectedRunHashes);
        Assert.Equal(2, Assert.Single(report.Requirements).Observations.Count);
        Assert.Throws<RequirementStatusException>(() => resolver.Resolve(Catalogue(), new("agent:t", "1"), [evidence], Expiry, evidence.PolicyDigest));
        Assert.Throws<RequirementStatusException>(() => resolver.Resolve(Catalogue(), new("agent:t", "1"), [evidence], Now, "wrong-policy"));
    }

    [Fact]
    public void Resolve_OneRunWaiver_CannotWaiveAnotherSelectedRun()
    {
        using var first = Run(out var firstHash);
        using var second = new WriterRun();
        var writer = AefRunWriter.Create(second.Dir, WriterRun.Header(runId: "run-other"));
        writer.SetMetrics([WriterRun.Metric()]);
        writer.AddResult(WriterRun.Leaf("other", state: AefState.Failed, m: 0.1));
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(5));
        var secondHash = AefSealer.Seal(second.Dir, WriterRun.Options()).RunHash;
        using var signer = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey(WriterRun.Alice, signer.PublicKey, [RequirementEvidenceReader.WaivePermission("team:quality")])]);
        Decision(first, AefOverlayKind.Waive, new() { Requirement = "team:quality" }, signer);
        var reader = new RequirementEvidenceReader();
        var evidence = new[] { reader.Read(new(first.Dir, firstHash), Now, policy), reader.Read(new(second.Dir, secondHash), Now, policy) };
        var report = new RequirementStatusResolver().Resolve(Catalogue(), new("agent:t", "1"), evidence, Now, RequirementEvidenceReader.PolicyDigest(policy));
        Assert.Equal("not_met", Assert.Single(report.Requirements).Status);
        Assert.Single(report.Requirements[0].Waivers);
    }

    [Fact]
    public void Resolve_ConflictingOverlaySnapshots_RefusesOrderDependentDeduplication()
    {
        using var run = Run(out var hash);
        var reader = new RequirementEvidenceReader();
        var before = reader.Read(new(run.Dir, hash), Now);
        Decision(run, AefOverlayKind.Approve, new());
        var after = reader.Read(new(run.Dir, hash), Now);
        Assert.NotEqual(before.OverlayDigest, after.OverlayDigest);
        Assert.Throws<RequirementStatusException>(() => new RequirementStatusResolver().Resolve(Catalogue(), new("agent:t", "1"),
            [before, after], Now, before.PolicyDigest));
    }

    [Theory]
    [InlineData(AefTargetMode.Live, true, "not_met")]
    [InlineData(AefTargetMode.Live, false, "not_measured")]
    [InlineData(AefTargetMode.Mocked, true, "not_measured")]
    [InlineData(AefTargetMode.Replayed, true, "not_measured")]
    public void Resolve_SuiteDigestAndLiveMode_RequireExactBindings(AefTargetMode mode, bool matching, string expected)
    {
        using var run = new WriterRun();
        var digest = "sha256:" + new string('a', 64);
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header() with
        {
            Suite = new AefSuite { Ref = "suite:quality", Version = "1", Frozen = true, Digest = digest },
            Execution = new AefExecution { TargetMode = mode },
        });
        writer.SetMetrics([WriterRun.Metric()]);
        writer.AddResult(WriterRun.Leaf("k1", state: AefState.Failed, m: 0.4));
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q" }])] });
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(5));
        var hash = AefSealer.Seal(run.Dir, WriterRun.Options()).RunHash;
        var source = $$"""
            {"schemaVersion":"1.0","requirements":[{"id":"team:quality",
             "selector":{"lane":"main","metric":"m","path":"q","suiteRef":"suite:quality","suiteDigest":"{{(matching ? digest : "sha256:" + new string('b', 64))}}"},
             "acceptance":{"kind":"threshold","operator":">=","value":0.8},
             "aggregation":{"kind":"all","missing":"not_measured"},"freshnessSeconds":86400}]}
            """;
        var catalogue = new RequirementCatalogueReader().Read(Encoding.UTF8.GetBytes(source));
        Assert.Equal(expected, Resolve(catalogue, run, hash).Status);
    }
}
