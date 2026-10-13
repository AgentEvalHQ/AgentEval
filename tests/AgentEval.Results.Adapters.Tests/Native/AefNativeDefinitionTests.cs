using System.Text;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefNativeDefinitionTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("native-definition");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_root);

    private static SuiteDefinitionManifest Definition(bool frozen = true) => new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes($$"""
        {"schemaVersion":"1.0","kind":"probe","suiteRef":"redteam:test","definitionVersion":"definition-1",
         "frozen":{{(frozen ? "true" : "false")}},"inputs":{"probes":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
         "definition":{"threshold":80,"generator":"fixture-v1"},"caseIds":["a","b"]}
        """));

    private static AefProbeRunOptions Options() => new()
    {
        SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "redteam:test", Definition = Definition(),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_OutcomesAndInterruption_RetainDeclaredIdentity(bool interrupted)
    {
        AefProbeLine[] lines = interrupted ? [new() { CaseId = "a", State = AefState.Failed }]
            : [new() { CaseId = "a", State = AefState.Passed }, new() { CaseId = "b", State = AefState.Passed }];
        var (dir, _) = AefProbeRunWriter.Write(_root, lines,
            Options() with { AbortReason = interrupted ? "cancelled" : null }, Start, Start.AddSeconds(1));
        var header = AefTestRuns.Document(dir, "run.json");
        Assert.Equal(Definition().Digest, (string?)header["suite"]!["digest"]);
        Assert.Equal("definition-1", (string?)header["suite"]!["version"]);
        Assert.Equal("definition", (string?)header["ext"]!["agenteval.suiteIdentity"]!["scope"]);
        Assert.Equal(lines.Length, (int)header["ext"]!["agenteval.observedRoster"]!["count"]!);
        Assert.Equal(Definition().ToBytes(), File.ReadAllBytes(Path.Combine(dir, "ext", SuiteDefinitionManifest.ExtensionFile)));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("undeclared", true)]
    public void Write_IncompleteOrUndeclaredRoster_RefusesBeforeCreatingRun(string id, bool interrupted)
    {
        Assert.Throws<ArgumentException>(() => AefProbeRunWriter.Write(_root,
            [new() { CaseId = id, State = AefState.Passed }],
            Options() with { AbortReason = interrupted ? "cancelled" : null }, Start, Start.AddSeconds(1)));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_GeneratedRecipe_DoesNotClaimFrozenStimuli(bool hasDefinition)
    {
        var (dir, _) = AefProbeRunWriter.Write(_root,
            [new() { CaseId = "generated-case", State = AefState.Passed }],
            Options() with { Stimulus = AefStimulus.Generated, Definition = hasDefinition ? Definition(false) : null },
            Start, Start.AddSeconds(1));
        var header = AefTestRuns.Document(dir, "run.json");
        Assert.Null(header["suite"]!["digest"]);
        Assert.False((bool)header["suite"]!["frozen"]!);
        Assert.Equal(1, (int)header["ext"]!["agenteval.observedRoster"]!["count"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
    }
}
