// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

/// <summary>
/// <see cref="StoreV1Exporter"/> on a store written by the real <c>FileSystemOutputStore</c> (§7.5): the imported run's
/// header, a line per scenario and per assertion with §7.5's states, the evidence, the summary, the seal, and both
/// verifiers' verdict.
/// </summary>
public sealed class StoreV1ExporterTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("aef-export");

    public void Dispose() => AefTestRuns.Delete(_out);

    [Fact]
    public async Task Export_WritesAnImportedRun_SealedByIngest_IntactUnderBothVerifiers()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        var conversion = await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out);

        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        Assert.Empty(conversion.Verification.Problems);
        Assert.Equal(AefSealedBy.Ingest.ToString().ToLowerInvariant(), (string)AefTestRuns.Document(_out, "seal.json")["predicate"]!["sealedBy"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(_out).Outcome);
        if (AefTestRuns.ReferenceVerify(_out) is { } reference)
        {
            Assert.Equal("intact", (string)reference["outcome"]!);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Fact]
    public async Task Export_RunJson_NamesTheExporterAndTheStore_AndListsWhatItSupplied()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        var conversion = await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out);

        var run = AefTestRuns.Document(_out, "run.json");
        Assert.Equal(store.RunId, (string)run["runId"]!);
        Assert.Equal("completed", (string)run["status"]!);
        Assert.Equal("agenteval-cli", (string)run["producer"]!["name"]!);
        Assert.StartsWith("agenteval store v1 (AgentEval ", (string)run["imported"]!["from"]!, StringComparison.Ordinal);
        Assert.Equal(["execution.targetMode", "contentCapture"], run["imported"]!["asserted"]!.AsArray().Select(a => (string)a!));
        Assert.Equal(conversion.Asserted, run["imported"]!["asserted"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("agent:Support%20Agent", (string)run["subject"]!["ref"]!);
        Assert.Equal("1.4.2", (string)run["subject"]!["version"]!);
        Assert.Equal("mocked", (string)run["execution"]!["targetMode"]!);
        Assert.Equal("on", (string)run["contentCapture"]!);
        Assert.Null(run["suite"]);
        Assert.Null(run["deployment"]);
        var judge = run["judges"]!.AsArray().Single()!;
        Assert.Equal((StoreV1Fixture.JudgeModel, StoreV1Fixture.PromptHash), ((string)judge["model"]!, (string)judge["rubricDigest"]!));
        Assert.Equal("StoreV1Fixture", (string)run["ext"]!["agenteval.store-v1"]!["run"]!["harness"]!);

        // What store v1 lacks is said, not filled in.
        Assert.Contains(conversion.Notes, n => n.Contains("no suite version or digest", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("no deployment", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("execution.targetMode is the exporter's claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_MapsEachScenarioToARootLine_AndEachAssertionToAChild_WithSection75sStates()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out);

        var lines = AefTestRuns.Results(_out);
        var byCase = lines.Where(l => (string)l["path"]! == StoreV1Exporter.ScenarioPath).ToDictionary(l => (string)l["caseId"]!);
        Assert.Equal(4, byCase.Count);
        Assert.Equal(4 + 3, lines.Count);

        // A plain scenario: its pass flag; three assertion children: passed, failed, and the undecidable one not_measured.
        var refund = byCase["refund-ok"];
        Assert.Equal("passed", (string)refund["state"]!);
        Assert.Null(refund["severity"]);
        var children = lines.Where(l => (string?)l["parentResultId"] == (string)refund["resultId"]!).ToList();
        Assert.Equal(["passed", "failed", "not_measured"], children.Select(c => (string)c["state"]!));
        Assert.Equal(["scenario/assertions/1", "scenario/assertions/2", "scenario/assertions/3"], children.Select(c => (string)c["path"]!));
        Assert.Equal("HaveCallCount", (string)children[1]["evaluator"]!["id"]!);
        Assert.Contains("declared tools", (string)children[2]["reason"]!, StringComparison.Ordinal);
        Assert.All(children, c => Assert.Equal((0.0, false), ((double)c["component"]!["weight"]!, (bool)c["component"]!["required"]!)));
        Assert.Equal((3, 2), ((int)refund["aggregation"]!["total"]!, (int)refund["aggregation"]!["measured"]!));
        Assert.Equal(1, (int)refund["aggregation"]!["unmeasured"]!["not_measured"]!);
        Assert.Equal(1500.0, (double)refund["durationMs"]!);

        // An eval-result tree: its label and severity, its judge as the annotator.
        var pii = byCase["pii-leak"];
        Assert.Equal(("failed", "high"), ((string)pii["state"]!, (string)pii["severity"]!));
        Assert.Equal(("privacy.pii", "2.1"), ((string)pii["evaluator"]!["id"]!, (string)pii["evaluator"]!["version"]!));
        Assert.Equal(("LLM", StoreV1Fixture.JudgeModel), ((string)pii["annotator"]!["kind"]!, (string)pii["annotator"]!["model"]!));
        Assert.Equal(0.5, (double)pii["verdictRule"]!["threshold"]!);
        Assert.Contains(pii["scores"]!.AsArray(), s => (string)s!["metric"]! == "accuracy" && (double)s["value"]! == 0.3);
        Assert.Contains("E-compliance-1", pii["evidence"]!.AsArray().Select(e => (string)e!));

        // Typed absences carry a reason and no score (RES-2).
        Assert.Equal("skipped", (string)byCase["timing"]["state"]!);
        Assert.Equal("No tool timing was recorded.", (string)byCase["timing"]["reason"]!);
        Assert.Equal("not_applicable", (string)byCase["no-context"]["state"]!);
        Assert.All(new[] { byCase["timing"], byCase["no-context"] }, l => Assert.Null(l["scores"]));
    }

    [Fact]
    public async Task Export_Summary_RecomputesThePassRateAndMeanScore_AndKeepsTheStoresOwnSummary()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out);

        var summary = AefTestRuns.Document(_out, "summary.json");
        var lane = summary["lanes"]!.AsArray().Single()!;
        Assert.Equal("benchmark", (string)lane["lane"]!);
        var pass = lane["metrics"]!.AsArray().Single(m => (string)m!["metric"]! == StoreV1Exporter.PassMetric)!;
        // refund-ok passed, pii-leak failed, timing skipped (not measured); no-context not_applicable (left out).
        Assert.Equal((3, 2, 1, 0.5, "scored"), ((int)pass["N"]!, (int)pass["n"]!, (int)pass["notMeasured"]!, (double)pass["value"]!, (string)pass["verdict"]!));
        var score = lane["metrics"]!.AsArray().Single(m => (string)m!["metric"]! == StoreV1Exporter.ScoreMetric)!;
        Assert.Equal(0.65, (double)score["value"]!, 12);
        Assert.Equal(0.003, (double)summary["cost"]!["totalUsd"]!);
        Assert.Equal("FAIL", (string)summary["ext"]!["agenteval.store-v1"]!["verdict"]!);
    }

    [Fact]
    public async Task Export_CarriesContentAsEvidence_AndTheComplianceEvidence()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out);

        var evidence = File.ReadAllLines(Path.Combine(_out, "evidence.ndjson")).Select(l => JsonNode.Parse(l)!).ToDictionary(e => (string)e["evidenceId"]!);
        Assert.Equal("compliance_artifact", (string)evidence["E-compliance-1"]!["kind"]!);
        Assert.Equal("transcript", (string)evidence["E-trace"]!["kind"]!);

        // The plain scenario cites its input, its output and the run's trace (which names it).
        var refund = AefTestRuns.Results(_out).Single(l => (string)l["caseId"]! == "refund-ok" && (string)l["path"]! == StoreV1Exporter.ScenarioPath);
        var cited = refund["evidence"]!.AsArray().Select(e => evidence[(string)e!]!).ToList();
        Assert.Equal(["input", "output", "transcript"], cited.Select(e => (string)e["kind"]!));
        var input = (string)cited[0]["link"]!["blob"]!;
        Assert.Equal("Refund order 42.", File.ReadAllText(Path.Combine(_out, "blobs", "sha256", input[7..9], input[7..])));

        // A scenario with an eval-result tree cites the tree, which may hold content, as "other".
        var pii = AefTestRuns.Results(_out).Single(l => (string)l["caseId"]! == "pii-leak" && (string)l["path"]! == StoreV1Exporter.ScenarioPath);
        Assert.Equal(["input", "other", "compliance_artifact"], pii["evidence"]!.AsArray().Select(e => (string)evidence[(string)e!]!["kind"]!));
    }

    [Fact]
    public async Task Export_WithContentCaptureOff_KeepsNoContent_AndStillVerifies()
    {
        using var store = await StoreV1Fixture.CreateAsync();

        var conversion = await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out, new StoreV1ExportOptions { ContentCapture = AefContentCapture.Off, TargetMode = AefTargetMode.Live });

        Assert.Equal(AefOutcome.Intact, conversion.Verification.Outcome);
        var run = AefTestRuns.Document(_out, "run.json");
        Assert.Equal(("off", "live"), ((string)run["contentCapture"]!, (string)run["execution"]!["targetMode"]!));
        var kinds = File.ReadAllLines(Path.Combine(_out, "evidence.ndjson")).Select(l => (string)JsonNode.Parse(l)!["kind"]!).ToList();
        Assert.Equal(["compliance_artifact"], kinds);
        var lines = AefTestRuns.Results(_out);
        Assert.All(lines, l => Assert.Null(l["ext"]?["agenteval.store-v1"]?["stimulusHash"]));
        Assert.DoesNotContain("social security", string.Join("\n", lines.Select(l => l.ToJsonString())), StringComparison.Ordinal);
        if (AefTestRuns.ReferenceVerify(_out) is { } reference)
        {
            Assert.Equal("intact", (string)reference["outcome"]!);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Fact]
    public async Task Export_OfARunThatNeverCompleted_IsAborted_WithItsEndSupplied()
    {
        using var store = await StoreV1Fixture.CreateAsync(complete: false);

        var conversion = await StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out, new StoreV1ExportOptions { Seal = false });

        Assert.Equal(AefOutcome.Unsealed, conversion.Verification.Outcome);
        Assert.Empty(conversion.Verification.Problems);
        var run = AefTestRuns.Document(_out, "run.json");
        Assert.Equal("aborted", (string)run["status"]!);
        Assert.Contains("never completed", (string)run["abortReason"]!, StringComparison.Ordinal);
        Assert.Contains("endedAt", run["imported"]!["asserted"]!.AsArray().Select(a => (string)a!));
        Assert.False(File.Exists(Path.Combine(_out, "seal.json")));
        if (AefTestRuns.ReferenceVerify(_out) is { } reference)
        {
            Assert.Equal("unsealed", (string)reference["outcome"]!);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Fact]
    public async Task Export_RefusesAnOutputFolderThatIsNotEmpty_AndARunTheStoreDoesNotHave()
    {
        using var store = await StoreV1Fixture.CreateAsync();
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "notes.txt"), "mine");

        await Assert.ThrowsAsync<ArgumentException>(() => StoreV1Exporter.ExportAsync(store.Store, store.RunId, _out));
        Assert.True(File.Exists(Path.Combine(_out, "notes.txt")));
        await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportAsync(store.Store, "2020-01-01_00-00-00_deadbeef", _out + "-x"));
    }
}
