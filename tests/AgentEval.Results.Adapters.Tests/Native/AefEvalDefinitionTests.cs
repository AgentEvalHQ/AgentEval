using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefEvalDefinitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-eval-definition-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Dataset = Encoding.UTF8.GetBytes("secret dataset content");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SuiteDefinitionManifest Definition() => new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes($$"""
        {"schemaVersion":"1.0","kind":"dataset","suiteRef":"dataset:cases.json","definitionVersion":"definition-1",
         "frozen":true,"inputs":{"dataset":"{{SuiteDefinitionManifest.Hash(Dataset)}}"},
         "definition":{"threshold":80,"runs":1},"caseIds":["c1","c2"]}
        """));
    private static AefEvalRunOptions Options() => new()
    {
        SubjectRef = "agent:example", SubjectVersion = "1", DatasetName = "cases.json", DatasetBytes = Dataset,
        TargetMode = AefTargetMode.Mocked, Definition = Definition(),
    };

    [Fact]
    public void Complete_DifferentOutcomesAndInterruption_RetainPlannedIdentityAndSealManifest()
    {
        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var session = AefEvalWriter.Start(_root, Options(), Start.AddMinutes(i));
            session.Add(new TestResult { TestName = "one", Score = i == 0 ? 90 : 40, Passed = i == 0 }, "c1", 80);
            if (i < 2) session.Add(new TestResult { TestName = "two", Score = 90, Passed = true }, "c2", 80);
            var (directory, _) = session.Complete(Start.AddMinutes(i).AddSeconds(5), abortReason: i == 2 ? "interrupted" : null);
            var run = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "run.json")))!;
            hashes.Add(run["suite"]!["digest"]!.GetValue<string>());
            Assert.Equal("definition", (string?)run["ext"]!["agenteval.suiteIdentity"]!["scope"]);
            var manifest = File.ReadAllBytes(Path.Combine(directory, "ext", SuiteDefinitionManifest.ExtensionFile));
            Assert.Equal(Definition().ToBytes(), manifest);
            Assert.DoesNotContain("secret dataset", Encoding.UTF8.GetString(manifest));
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            if (AefTestRuns.ReferenceVerify(directory) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
            var summary = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "summary.json")))!;
            Assert.Equal(i == 2 ? 1 : 2, (int)summary["ext"]!["agenteval.observedRoster"]!["count"]!);
        }
        Assert.Single(hashes.Distinct());
        Assert.Equal(Definition().Digest, hashes[0]);
    }

    [Fact]
    public void Start_DifferentSnapshot_RefusesBeforeCreatingEvidence()
    {
        Assert.Throws<ArgumentException>(() => AefEvalWriter.Start(_root,
            Options() with { DatasetBytes = "changed"u8.ToArray() }, Start));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Add_UndeclaredCase_RefusesBeforeWritingResult()
    {
        using var session = AefEvalWriter.Start(_root, Options(), Start);
        Assert.Throws<ArgumentException>(() => session.Add(new TestResult { TestName = "unknown", Score = 90, Passed = true }, "not-planned"));
        Assert.Empty(File.ReadAllBytes(Path.Combine(session.Directory, "results.ndjson")));
    }

    [Fact]
    public void Start_RunningSnapshot_AlreadyRetainsCompleteDefinition()
    {
        using var session = AefEvalWriter.Start(_root, Options(), Start);
        Assert.Equal(Definition().ToBytes(), File.ReadAllBytes(Path.Combine(session.Directory, "ext", SuiteDefinitionManifest.ExtensionFile)));
        var header = JsonNode.Parse(File.ReadAllBytes(Path.Combine(session.Directory, "run.json")))!;
        Assert.Equal(2, (int)header["ext"]!["agenteval.suiteIdentity"]!["plannedCases"]!);
    }

    [Fact]
    public void Complete_MissingPlannedCases_RequiresExplicitAbortedStatus()
    {
        using var session = AefEvalWriter.Start(_root, Options(), Start);
        session.Add(new TestResult { TestName = "one", Passed = true, Score = 90 }, "c1");
        Assert.Throws<InvalidOperationException>(() => session.Complete(Start.AddSeconds(5)));
        Assert.False(File.Exists(Path.Combine(session.Directory, "seal.json")));
        var (directory, _) = session.Complete(Start.AddSeconds(5), abortReason: "stopped early");
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
    }
}
