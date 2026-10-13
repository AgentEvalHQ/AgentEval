// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefProbeRunWriterTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("aef-probes");

    public void Dispose() => AefTestRuns.Delete(_out);

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ObservedUsage_ReplacesLegacyRoleWithoutDoubleCounting(bool unknown)
    {
        var legacy = new AefUsage { Role = AefUsageRole.Judge, Model = "deployment", InputTokens = 999, CostUsd = 5 };
        AefUsage[] observed = [new() { Role = AefUsageRole.Judge, Model = "reported-model",
            InputTokens = unknown ? null : 10, CostUsd = unknown ? null : 0.1, CostSource = unknown ? null : "fixture" }];
        var (dir, _) = AefProbeRunWriter.Write(_out,
            [new() { CaseId = "case", State = AefState.Passed, Annotator = AefAnnotatorKind.Code, Usage = observed }],
            Options() with { JudgeUsage = legacy, Usage = observed }, Start, Start.AddSeconds(1));
        var lineUsage = Assert.Single(Assert.Single(AefTestRuns.Results(dir))["usage"]!.AsArray())!;
        Assert.Equal("reported-model", (string?)lineUsage["model"]);
        Assert.Equal(unknown ? (long?)null : 10, (long?)lineUsage["gen_ai.usage.input_tokens"]);
        var summary = AefTestRuns.Document(dir, "summary.json");
        var usage = Assert.Single(summary["usage"]!.AsArray())!;
        Assert.Equal("reported-model", (string?)usage["model"]);
        Assert.Equal(unknown ? (long?)null : 10, (long?)usage["gen_ai.usage.input_tokens"]);
        if (unknown) Assert.Null(summary["cost"]);
        else Assert.Equal(0.1, (double)summary["cost"]!["totalUsd"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Fact]
    public void Write_DistinctObservedRubrics_RetainsHeadersAndMatchingAnnotators()
    {
        var first = "sha256:" + new string('a', 64);
        var second = "sha256:" + new string('b', 64);
        var (dir, _) = AefProbeRunWriter.Write(_out,
            [new() { CaseId = "a", State = AefState.Passed, Annotator = AefAnnotatorKind.Llm, RubricDigest = first },
             new() { CaseId = "b", State = AefState.Failed, Annotator = AefAnnotatorKind.Llm, RubricDigest = second },
             new() { CaseId = "c", State = AefState.Passed, Annotator = AefAnnotatorKind.Llm, RubricDigest = first },
             new() { CaseId = "d", State = AefState.Passed, Annotator = AefAnnotatorKind.Code, RubricDigest = second }],
            Options(), Start, Start.AddSeconds(1));
        Assert.Equal(new[] { first, second }, AefTestRuns.Document(dir, "run.json")["judges"]!.AsArray()
            .Select(j => (string?)j!["rubricDigest"]));
        var lines = AefTestRuns.Results(dir);
        Assert.Equal(new string?[] { first, second, first, null }, lines.Select(l => (string?)l["annotator"]?["rubricDigest"]));
        Assert.All(lines, l => Assert.Null(l["annotator"]?["promptHash"]));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("configured-provider", false)]
    [InlineData("configured-provider", true)]
    public void Write_ProviderIdentity_RecordsOnlyObservedJudge(string? provider, bool codeOnly)
    {
        var (dir, _) = AefProbeRunWriter.Write(_out,
            [new AefProbeLine { CaseId = "probe", State = AefState.Passed,
                Annotator = codeOnly ? AefAnnotatorKind.Code : AefAnnotatorKind.Llm }],
            Options() with { JudgeProvider = provider }, Start, Start.AddSeconds(1));
        var judges = AefTestRuns.Document(dir, "run.json")["judges"];
        if (codeOnly) Assert.Null(judges);
        else Assert.Equal(provider, (string?)judges![0]!["provider"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Fact]
    public void Write_InterruptedTrialStoreLinks_PreservesOnlyRecordedIdentities()
    {
        var (dir, _) = AefProbeRunWriter.Write(_out, [], new AefProbeRunOptions
        {
            SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "bench:test",
            AbortReason = "cancelled", StoreV1 = new() { TrialRunIds = ["trial-1"] },
        }, Start, Start.AddSeconds(1));
        var link = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "run.json")))!["ext"]!["agenteval.store-v1"]!;
        Assert.Null(link["runId"]);
        Assert.Equal("trial-1", (string?)link["trialRunIds"]![0]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
    }

    private static AefAttack Attack(bool? success) => new()
    {
        Technique = "PromptInjection",
        Taxonomy = [new AefTaxonomyId(AefTaxonomyScheme.OwaspLlm, "LLM01")],
        Success = success,
    };

    private static AefProbeRunOptions Options() => new()
    {
        SubjectRef = "model:gpt-x",
        Endpoint = "https://user:pass@example.com/v1",
        JudgeModel = "judge-x",
        TargetMode = AefTargetMode.Mocked,
        SuiteRef = "redteam:default/quick",
        ProbesNotRun = 2,
    };

    [Fact]
    public void Write_RecordsEveryState_AndTheBenignControls_IntactUnderBothVerifiers()
    {
        AefProbeLine[] lines =
        [
            new() { CaseId = "PromptInjection/p1", State = AefState.Passed, Severity = AefSeverity.High, Attack = Attack(false), Annotator = AefAnnotatorKind.Code },
            new() { CaseId = "PromptInjection/p2", State = AefState.Failed, Severity = AefSeverity.High, Attack = Attack(true), Annotator = AefAnnotatorKind.Llm, Turns = 3 },
            new() { CaseId = "PromptInjection/p3", State = AefState.Inconclusive, Attack = Attack(null), Annotator = AefAnnotatorKind.Code },
            new() { CaseId = "PromptInjection/p4", State = AefState.Error, Reason = "the probe timed out", Attack = Attack(null) },
            new() { CaseId = "SystemPromptExtraction/p1", State = AefState.NotApplicable, Reason = "no canary was planted", Attack = Attack(null) },
            new() { CaseId = "benign/b1", Control = true, State = AefState.Passed, Annotator = AefAnnotatorKind.Llm },
            new() { CaseId = "benign/b2", Control = true, State = AefState.Failed, Annotator = AefAnnotatorKind.Llm },
        ];

        var (dir, _) = AefProbeRunWriter.Write(_out, lines, Options(), Start, Start.AddMinutes(1));

        var verification = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        var run = AefTestRuns.Document(dir, "run.json");
        Assert.Equal("judge-x", (string?)run["judges"]![0]!["model"]);   // the judge graded p2 and the controls
        Assert.Equal("mocked", (string?)run["execution"]!["targetMode"]);
        Assert.Equal(AefProbeRunWriter.RosterDigest(lines.Select(l => l.CaseId)), (string?)run["suite"]!["digest"]);

        var written = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject())
            .ToDictionary(l => (string)l["caseId"]!);
        Assert.Null(written["PromptInjection/p1"]["severity"]);   // severity is a failure's
        Assert.Equal("high", (string?)written["PromptInjection/p2"]["severity"]);
        Assert.Equal("over-refusal", (string?)written["benign/b1"]["lane"]);
        Assert.Equal("control", (string?)written["benign/b1"]["path"]);
        Assert.Null(written["PromptInjection/p3"]["scores"]);   // resisted-conclusive is on conclusive lines only

        var summary = AefTestRuns.Document(dir, "summary.json");
        var redteam = summary["lanes"]!.AsArray().Single(l => (string?)l!["lane"] == "redteam")!["metrics"]!.AsArray();
        // resisted: p1 of p1, p2, p3 (p3 inconclusive counts 0; the error and not_applicable are out).
        Assert.Equal(1.0 / 3, (double)redteam.Single(m => (string?)m!["metric"] == "resisted")!["value"]!);
        // resisted-conclusive: p1 of p1, p2.
        Assert.Equal(0.5, (double)redteam.Single(m => (string?)m!["metric"] == "resisted-conclusive")!["value"]!);
        var answered = summary["lanes"]!.AsArray().Single(l => (string?)l!["lane"] == "over-refusal")!["metrics"]![0]!;
        Assert.Equal(0.5, (double)answered["value"]!);
        Assert.Equal(2, (int)summary["ext"]!["agenteval.redteam"]!["probesNotRun"]!);
    }

    [Fact]
    public void Write_NamesNoJudge_WhenOnlyCodeGraded()
    {
        AefProbeLine[] lines = [new() { CaseId = "PromptInjection/p1", State = AefState.Passed, Annotator = AefAnnotatorKind.Code }];

        var (dir, _) = AefProbeRunWriter.Write(_out, lines, Options(), Start, Start.AddMinutes(1));

        Assert.Null(AefTestRuns.Document(dir, "run.json")["judges"]);
    }

    [Fact]
    public void RosterDigest_DoesNotDependOnOrderOrRepeats() =>
        Assert.Equal(AefProbeRunWriter.RosterDigest(["b", "a", "a"]), AefProbeRunWriter.RosterDigest(["a", "b"]));

    [Theory]
    [InlineData("vuln", 1, AefGateOutcome.NoShip)]
    [InlineData("regression", 4, AefGateOutcome.NoShip)]
    [InlineData("never", 0, AefGateOutcome.Ship)]
    public void Write_CommandGateAndUsage_PreservesPolicyEvenWithFailedProbe(string policy, int exitCode, AefGateOutcome outcome)
    {
        var options = Options() with
        {
            Gate = new AefCommandGate { GateId = "gate:redteam", Rule = "fail-on:" + policy, ExitCode = exitCode, Outcome = outcome },
            JudgeUsage = new AefUsage { Role = AefUsageRole.Judge, Model = "judge-x", InputTokens = 123, OutputTokens = 45 },
        };
        var (dir, _) = AefProbeRunWriter.Write(_out,
            [new AefProbeLine { CaseId = "attack/p1", State = AefState.Failed, Annotator = AefAnnotatorKind.Llm }],
            options, Start, Start.AddMinutes(1));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
            Assert.Equal("intact", (string?)reference["outcome"]);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal("fail-on:" + policy, (string?)gate["rule"]!["strategy"]);
        Assert.Equal(exitCode, (int)gate["exitCode"]!);
        Assert.Equal(exitCode == 0 ? "ship" : "no_ship", (string?)gate["outcome"]);
        Assert.Single(gate["inputs"]!["results"]!.AsArray());
        var usage = AefTestRuns.Document(dir, "summary.json")["usage"]![0]!;
        Assert.Equal(123, (long)usage["gen_ai.usage.input_tokens"]!);
        Assert.Equal(45, (long)usage["gen_ai.usage.output_tokens"]!);
    }
}
