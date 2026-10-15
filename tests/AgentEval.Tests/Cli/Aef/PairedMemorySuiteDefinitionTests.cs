using System.Text.Json;
using AgentEval.Cli.Infrastructure;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

public sealed class PairedMemorySuiteDefinitionTests
{
    private static ExternalBenchmarkPlan Source(string id = "q1", string history = "private history", int retries = 0) => new(
        new ExternalBenchmarkOptions { MaxJudgeRetries = retries }, [new LongMemEvalEntry
        {
            QuestionId = id, QuestionType = "single-session-user", Question = "private question",
            AnswerRaw = JsonSerializer.SerializeToElement("private gold"),
            HaystackSessions = [[new() { Role = "user", Content = history, HasAnswer = true }]],
            HaystackDates = ["2026/07/01"], HaystackSessionIds = ["s1"], AnswerSessionIds = ["s1"],
        }]);
    private static LongMemEvalPairedPlan Pair(ExternalBenchmarkPlan source, string model = "model-a") => new(source,
        new AgentBenchmarkConfig { AgentName = "normal", ModelId = model, Temperature = 0 },
        new AgentBenchmarkConfig { AgentName = "oracle", ModelId = model, Temperature = 0 }, "judge-a");

    [Fact]
    public void Create_SameSourceAndRepeatedExecutions_KeepStableArmDefinitionsWithDistinctPairIds()
    {
        var source = Source(); var first = Pair(source); var second = Pair(source);
        var normal = PairedMemorySuiteDefinition.Create("subset", first, LongMemEvalArm.Normal, source, "fixture");
        var repeated = PairedMemorySuiteDefinition.Create("subset", second, LongMemEvalArm.Normal, source, "fixture");
        var oracle = PairedMemorySuiteDefinition.Create("subset", first, LongMemEvalArm.Oracle, source, "fixture");
        Assert.NotEqual(first.PairId, second.PairId);
        Assert.Equal(normal.Digest, repeated.Digest);
        // Even when projection preserves every byte (all source evidence is gold), role identity stays distinct.
        Assert.NotEqual(normal.Digest, oracle.Digest);
        Assert.Equal("bench:longmemeval/subset/normal", normal.SuiteRef);
        Assert.Equal("bench:longmemeval/subset/oracle", oracle.SuiteRef);
        Assert.Equal(source.SelectedInputsDigest, (string?)oracle.ToJson()["inputs"]!["pairedSource"]);
        Assert.True(normal.Frozen);
        Assert.True(oracle.Frozen);
        Assert.Equal(source.QuestionIds, oracle.CaseIds);
        Assert.DoesNotContain(first.PairId, normal.ToJson().ToJsonString());
        Assert.DoesNotContain("private", normal.ToJson().ToJsonString());
        var restored = JsonSerializer.Deserialize<LongMemEvalPairedPlan>(JsonSerializer.Serialize(first))!;
        Assert.Equal(normal.Digest, PairedMemorySuiteDefinition.Create("subset", restored, LongMemEvalArm.Normal,
            restored.Source, "fixture").Digest);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("projection")]
    [InlineData("model")]
    [InlineData("provider")]
    public void Create_ChangesToPairedWorkloadOrArmConfiguration_ChangeDefinition(string change)
    {
        var source = Source(); var pair = Pair(source); var oracle = Source(history: "projected");
        var before = PairedMemorySuiteDefinition.Create("subset", pair, LongMemEvalArm.Oracle, oracle, "fixture");
        if (change == "source") pair = Pair(Source(history: "different source"));
        if (change == "projection") oracle = Source(history: "different projection");
        if (change == "model") pair = Pair(source, "model-b");
        var after = PairedMemorySuiteDefinition.Create("subset", pair, LongMemEvalArm.Oracle, oracle,
            change == "provider" ? "other" : "fixture");
        Assert.NotEqual(before.Digest, after.Digest);
    }

    [Theory]
    [InlineData("roster")]
    [InlineData("options")]
    [InlineData("normal-inputs")]
    [InlineData("unknown-arm")]
    public void Create_ArmNotBoundToPair_Refuses(string change)
    {
        var source = Source();
        var arm = change == "roster" ? Source("other") : change == "options" ? Source(retries: 1)
            : change == "normal-inputs" ? Source(history: "other") : source;
        Assert.Throws<ArgumentException>(() => PairedMemorySuiteDefinition.Create("subset", Pair(source),
            change == "unknown-arm" ? (LongMemEvalArm)999 : LongMemEvalArm.Normal, arm, "fixture"));
    }
}
