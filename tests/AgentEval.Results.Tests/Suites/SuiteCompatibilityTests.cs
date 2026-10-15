using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Suites;
using AgentEval.Results.Tests.Writing;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Suites;

public sealed class SuiteCompatibilityTests
{
    private static SuiteDefinitionManifest Definition(bool frozen = true, double threshold = 0.8) =>
        new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:test",
            ["definitionVersion"] = "1", ["frozen"] = frozen,
            ["inputs"] = new JsonObject { ["rubric"] = SuiteDefinitionManifest.Hash("fixture"u8) },
            ["definition"] = new JsonObject { ["threshold"] = threshold }, ["caseIds"] = new JsonArray("a"),
        }.ToJsonString()));

    private static void Write(WriterRun run, SuiteDefinitionManifest? definition, bool aborted = false,
        Action<JsonObject>? alterMetadata = null, Func<AefSuite, AefSuite>? alterSuite = null,
        bool writeManifest = true, bool seal = true, string? legacyScope = null, bool generated = false)
    {
        var metadata = definition?.Metadata() ?? new JsonObject { ["scope"] = legacyScope };
        alterMetadata?.Invoke(metadata);
        var suite = new AefSuite { Ref = "bench:test", Version = "1", Frozen = definition?.Frozen ?? false, Digest = definition?.SuiteContentDigest };
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header() with
        {
            Suite = alterSuite?.Invoke(suite) ?? suite,
            Ext = new JsonObject { ["agenteval.suiteIdentity"] = metadata },
            Execution = new AefExecution { TargetMode = AefTargetMode.Live, Stimulus = generated ? AefStimulus.Generated : AefStimulus.Suite },
        });
        writer.SetMetrics([WriterRun.Metric()]);
        if (writeManifest && definition is not null) writer.PutExtFile(SuiteDefinitionManifest.ExtensionFile, definition.ToBytes());
        if (!aborted) writer.AddResult(WriterRun.Leaf("a"));
        writer.Close(aborted ? AefRunStatus.Aborted : AefRunStatus.Completed, WriterRun.Start.AddSeconds(1), aborted ? "cancelled" : null);
        if (seal) AefSealer.Seal(run.Dir, WriterRun.Options());
    }

    [Fact]
    public void Compare_CompletedAndEmptyAbortedSameDefinition_CompatibleWithExplicitStatusesAndHashes()
    {
        using var baseline = new WriterRun(); using var candidate = new WriterRun();
        Write(baseline, Definition()); Write(candidate, Definition(), aborted: true);
        var reader = new SuiteIdentityReader();
        var first = reader.Read(baseline.Dir);
        var second = reader.Read(candidate.Dir);
        var report = new SuiteCompatibilityEvaluator().Compare(first, second);
        Assert.True(report.Compatible);
        Assert.Equal("same-definition", report.Reason);
        Assert.Equal("completed", first.RunStatus);
        Assert.Equal("aborted", second.RunStatus);
        Assert.NotEqual(first.RunHash, second.RunHash);
        Assert.Equal(first.RunHash, reader.Read(baseline.Dir, first.RunHash).RunHash);
        Assert.Equal(second.RunHash, (string?)report.ToJson()["candidate"]!["runHash"]);
        File.WriteAllText(candidate.Full("run.json"), "changed after snapshot");
        Assert.True(new SuiteCompatibilityEvaluator().Compare(first, second).Compatible);
    }

    [Fact]
    public void Compare_ChangedThresholdWithSameRoster_Incompatible()
    {
        using var baseline = new WriterRun(); using var candidate = new WriterRun();
        Write(baseline, Definition()); Write(candidate, Definition(threshold: 0.9));
        var reader = new SuiteIdentityReader();
        var result = new SuiteCompatibilityEvaluator().Compare(reader.Read(baseline.Dir), reader.Read(candidate.Dir));
        Assert.False(result.Compatible);
        Assert.Equal("definition-mismatch", result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dataset")]
    [InlineData("observed-roster")]
    [InlineData("legacy-roster")]
    [InlineData("unrecognized")]
    public void Compare_LegacyOrMissingIdentityEvenWithEqualDigest_Incompatible(string? scope)
    {
        using var run = new WriterRun();
        Write(run, null, legacyScope: scope, alterSuite: s => s with { Digest = Definition().Digest, Frozen = true });
        var identity = new SuiteIdentityReader().Read(run.Dir);
        var result = new SuiteCompatibilityEvaluator().Compare(identity, identity);
        Assert.False(result.Compatible);
        Assert.Equal("definition-required", result.Reason);
        Assert.Null(identity.Definition);
    }

    [Fact]
    public void Compare_IdenticalRecipesWithAbsentContentDigests_Incompatible()
    {
        using var run = new WriterRun();
        Write(run, Definition(false), generated: true);
        var identity = new SuiteIdentityReader().Read(run.Dir);
        var result = new SuiteCompatibilityEvaluator().Compare(identity, identity);
        Assert.False(result.Compatible);
        Assert.Equal("generated-content", result.Reason);
        Assert.Null(identity.ContentDigest);
        Assert.NotNull(identity.Definition);
    }

    [Fact]
    public void Compare_AbsentDigestsWithoutDefinitions_NeverTreatsNullAsContentIdentity()
    {
        using var run = new WriterRun();
        Write(run, null);
        var identity = new SuiteIdentityReader().Read(run.Dir);
        Assert.Null(identity.ContentDigest);
        Assert.False(new SuiteCompatibilityEvaluator().Compare(identity, identity).Compatible);
    }

    [Fact]
    public void Read_SealedGeneratedStimuliWithFrozenDeclaration_RefusesContradiction()
    {
        using var run = new WriterRun();
        Write(run, Definition(), generated: true);
        var error = Assert.Throws<FormatException>(() => new SuiteIdentityReader().Read(run.Dir));
        Assert.Contains("generated stimuli claim a frozen definition", error.Message);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("schemaVersion")]
    [InlineData("digest")]
    [InlineData("path")]
    [InlineData("frozen")]
    [InlineData("plannedCases")]
    public void Read_SealedButContradictoryMetadata_Refuses(string field)
    {
        using var run = new WriterRun();
        Write(run, Definition(), alterMetadata: m => m[field] = "incorrect");
        Assert.Throws<FormatException>(() => new SuiteIdentityReader().Read(run.Dir));
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("version")]
    [InlineData("frozen")]
    [InlineData("digest")]
    public void Read_SealedButContradictorySuiteHeader_Refuses(string field)
    {
        using var run = new WriterRun();
        Write(run, Definition(), alterSuite: s => field switch
        {
            "ref" => s with { Ref = "bench:other" }, "version" => s with { Version = "2" },
            "frozen" => s with { Frozen = false }, _ => s with { Digest = SuiteDefinitionManifest.Hash("other"u8) },
        });
        Assert.Throws<FormatException>(() => new SuiteIdentityReader().Read(run.Dir));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsealed")]
    [InlineData("tampered")]
    [InlineData("oversized")]
    [InlineData("hash-mismatch")]
    [InlineData("hash-malformed")]
    public void Read_UnverifiableEvidence_Refuses(string kind)
    {
        using var run = new WriterRun();
        Write(run, Definition(), writeManifest: kind != "missing", seal: kind != "unsealed");
        if (kind == "tampered") File.AppendAllText(run.Full("ext/" + SuiteDefinitionManifest.ExtensionFile), " ");
        if (kind == "oversized") File.WriteAllBytes(run.Full("ext/" + SuiteDefinitionManifest.ExtensionFile), new byte[SuiteDefinitionReader.MaxBytes + 1]);
        var hash = kind == "hash-mismatch" ? new string('0', 64) : kind == "hash-malformed" ? "bad" : null;
        var error = Assert.Throws<FormatException>(() => new SuiteIdentityReader().Read(run.Dir, hash));
        Assert.Contains("Expected:", error.Message);
        Assert.Contains("Actual:", error.Message);
        Assert.Contains("Suggestions:", error.Message);
        Assert.Contains("Because:", error.Message);
    }
}
