// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Cli.Pdf;
using AgentEval.Core.Reporting;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Prompts;
using AgentEval.Reporting;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AgentEval.Rendering.PdfSharp.Tests;

public sealed class CompliancePromptAttestationTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("pdf-attestation-");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Dispose() => directory.Delete(true);

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", false)]
    [InlineData("eu-ai-act", true)]
    public async Task Reporter_ConfiguredIdentity_RoundTripsThroughSealedPdfWithoutPromptContent(string family, bool captureContent)
    {
        var prompt = Prompt(captureContent);
        var report = await ReportAsync(family, [Leaf(prompt)], new Dictionary<string, string> { ["stale"] = "STALE-DECLARED-v1" });

        var identity = Assert.Single(report.Versions).Value;
        foreach (var value in new[] { prompt.EvaluatorKey, prompt.InstrumentId, prompt.Version, prompt.EffectiveLocale, prompt.Digest })
            Assert.Contains(value, identity);
        Assert.DoesNotContain("STALE-DECLARED-v1", identity);
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(report.Versions));
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(report.Document));
        Assert.Equal(captureContent, JsonSerializer.Serialize(report.Value, report.Value.GetType(), Json).Contains("PRIVATE-SYSTEM", StringComparison.Ordinal));

        var source = WriteCapturedRun(family, report, AefContentCapture.On);
        var before = Hashes(source);
        var destination = Path.Combine(directory.FullName, "report.pdf");
        Assert.Equal(0, await PdfCommand.RunAsync(["render", "--run", source, "--output", destination]));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(destination);
        var text = string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '\u200b'));
        foreach (var value in new[] { prompt.EvaluatorKey, prompt.InstrumentId, prompt.Version, prompt.EffectiveLocale, prompt.Digest })
            Assert.Contains(value, compact);
        Assert.DoesNotContain("PRIVATE-", text);
        Assert.DoesNotContain("STALE-DECLARED-v1", text);
        Assert.Equal(before, Hashes(source));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(source).Outcome);
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", false)]
    [InlineData("eu-ai-act", true)]
    public async Task Reporter_UnconfiguredTree_PreservesLegacyAttestation(string family, bool suppliedVersions)
    {
        var supplied = suppliedVersions ? new Dictionary<string, string> { ["customer-legacy"] = "legacy-7" } : null;
        var report = await ReportAsync(family, [Leaf(null)], supplied);

        if (supplied is not null)
        {
            Assert.Equal("legacy-7", Assert.Single(report.Versions).Value);
            Assert.True(report.Versions.ContainsKey("customer-legacy"));
        }
        else
        {
            Assert.Equal(2, report.Versions.Count);
            Assert.Equal("v1", report.Versions[family + "-judge-system"]);
            Assert.Equal("v1", report.Versions["per-criterion"]);
        }
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("eu-ai-act", false)]
    [InlineData("eu-ai-act", true)]
    public async Task Reporter_MixedInstruments_FlagsEachObservedIdentityWithoutChoosingOne(string family, bool includeLegacy)
    {
        var first = Prompt(false);
        var other = includeLegacy ? null : first with
        {
            Version = "8.0.0", EffectiveLocale = "en", Digest = "sha256:" + new string('b', 64)
        };
        var report = await ReportAsync(family, [Leaf(first), Leaf(other)]);
        var identity = Assert.Single(report.Versions).Value;
        Assert.Contains("MIXED", identity);
        Assert.Contains(first.Digest, identity);
        Assert.Contains(first.Version, identity);
        if (other is null) Assert.Contains("UNIDENTIFIED", identity);
        else
        {
            Assert.Contains(other.Version, identity);
            Assert.Contains(other.EffectiveLocale, identity);
            Assert.Contains(other.Digest, identity);
        }
        Assert.DoesNotContain(family + "-judge-system", report.Versions.Keys);
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public async Task Reporter_RepeatedIdentityAndCodeOnlyLeaf_DeduplicatesWithoutClaimingAmbiguity(string family)
    {
        var prompt = Prompt(false);
        var code = Leaf(null) with
        {
            Metric = new("code-check", "Code check", "test", "1"),
            Provenance = new("atomic-code", null, null, null, null, 0, false)
        };
        var report = await ReportAsync(family, [Leaf(prompt), Leaf(prompt), code]);
        var identity = Assert.Single(report.Versions).Value;
        Assert.Contains(prompt.Digest, identity);
        Assert.DoesNotContain("MIXED", identity);
        Assert.DoesNotContain("UNIDENTIFIED", identity);
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public async Task Reporter_ContentOffAef_KeepsIdentityAndOmitsCapturedPromptText(string family)
    {
        var prompt = Prompt(true);
        var report = await ReportAsync(family, [Leaf(prompt)]);
        var source = WriteCapturedRun(family, report, AefContentCapture.Off);
        var recorded = string.Join("\n", Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains(prompt.Digest, recorded);
        Assert.Contains(prompt.EffectiveLocale, recorded);
        Assert.DoesNotContain("PRIVATE-", recorded);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(source).Outcome);
    }

    [Fact]
    public void Attestation_CyclicTree_RejectsInsteadOfSilentlyDroppingProvenance()
    {
        var children = new List<EvalResult>();
        var tree = Node("cycle", children);
        children.Add(tree);
        var error = Assert.Throws<ArgumentException>(() => JudgePromptAttestation.FromResult(tree));
        Assert.Contains("maximum attestation depth exceeded", error.Message);
    }

    [Fact]
    public void Attestation_AggregateMetadata_DoesNotReplaceObservedLeafIdentity()
    {
        var prompt = Prompt(false);
        var tree = Node("aggregate", [Leaf(prompt)]) with
        {
            Provenance = new("composite", null, null, null, null, 0, false)
            {
                JudgePrompt = prompt with { InstrumentId = "STALE-AGGREGATE" }
            }
        };
        var identity = Assert.Single(JudgePromptAttestation.FromResult(tree)!).Value;
        Assert.Contains(prompt.InstrumentId, identity);
        Assert.DoesNotContain("STALE-AGGREGATE", identity);
    }

    private string WriteCapturedRun(string family, ReportFixture report, AefContentCapture capture)
        => AefCompositeRunWriter.Write(Path.Combine(directory.FullName, "runs"), report.Tree, AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:attested", TargetMode = AefTargetMode.Mocked, Lane = family, SuiteRef = $"bench:{family}/smoke",
            ContentCapture = capture,
            ComplianceArtifacts = [new()
            {
                Bytes = JsonSerializer.SerializeToUtf8Bytes(report.Value, report.Value.GetType(), Json), CaseIds = [report.ArticleKey]
            }]
        }, report.GeneratedAt.AddSeconds(-1), report.GeneratedAt).Directory;

    private static async Task<ReportFixture> ReportAsync(string family, EvalResult[] leaves, IReadOnlyDictionary<string, string>? versions = null)
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "attested");
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(subject, new("tests", "tests/", "prompt-attestation", null, null, "benchmark"));
        for (var index = 0; index < leaves.Length; index++)
            await store.WriteScenarioResultAsync(manifest.Run.RunId, EvalResultPersistence.ToScenarioResult(leaves[index], "case-" + index, "Scenario"));
        await store.CompleteRunAsync(manifest, new("1.0", manifest.Run.RunId, "PASS", new(leaves.Length, leaves.Length, 0, 0),
            new Dictionary<string, double> { ["overallScore"] = 0.8 }));
        if (family == "gdpr")
        {
            var registry = new AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry(
                new(), new(new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(new NeverJudge(), "unused")));
            var article = registry.All.Keys.Order(StringComparer.Ordinal).First();
            var tree = Node("root", [Node(article, leaves)]);
            var report = await new GDPRComplianceReporter(registry).SaveReportAsync(store, subject, manifest.Run.RunId, tree,
                new(Preset: "smoke", PromptVersions: versions));
            return new(report, tree, report.GdprAttestation.PromptVersions, new GDPRPdfRenderer().CreateDocument(report), report.Base.GeneratedAt, article);
        }
        else
        {
            var registry = new AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry(
                new(), new(new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(new NeverJudge(), "unused")));
            var article = registry.All.Keys.Order(StringComparer.Ordinal).First();
            var tree = Node("root", [Node(article, leaves)]);
            var report = await new EuAiActComplianceReporter(registry).SaveReportAsync(store, subject, manifest.Run.RunId, tree,
                new(Preset: "smoke", PromptVersions: versions));
            return new(report, tree, report.EuAiActAttestation.PromptVersions, new EuAiActPdfRenderer().CreateDocument(report), report.Base.GeneratedAt, article);
        }
    }

    private static JudgePromptProvenance Prompt(bool capture) => new("configured-criterion", "swedish-rubric", "7.2.0", "sv-SE", "sv-SE", "pack", false,
        "sha256:" + new string('a', 64), JudgeResponseContract.CriteriaV1)
    {
        Content = capture ? new("PRIVATE-SYSTEM", "PRIVATE-USER", "PRIVATE-RETRY") : null
    };

    private static EvalResult Leaf(JudgePromptProvenance? prompt) => Node("scenario", null) with
    {
        Provenance = new("atomic-llm", "recorded-model", prompt?.InstrumentId ?? "legacy-prompt", null, 10, 0, false) { JudgePrompt = prompt }
    };

    private static EvalResult Node(string key, IReadOnlyList<EvalResult>? children) => new(new(key, key, "compliance", "1"),
        new(0.8, null, "pass", true, 0.75, "none", null), new(null, null, null, children, null),
        new("composite", null, null, null, null, 0, false), DateTimeOffset.UtcNow);

    private static string[] Hashes(string source) => Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(source, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed record ReportFixture(object Value, EvalResult Tree, IReadOnlyDictionary<string, string> Versions, ReportDocument Document,
        DateTimeOffset GeneratedAt, string ArticleKey);

    private sealed class NeverJudge : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Reporting must not invoke a judge.");
    }
}
