using System.Text.Json.Nodes;
using AgentEval.Cli.Infrastructure;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

public sealed class TypedMemoryDiagnosticsTests
{
    private static QuestionResult Question(TypedMemEvalOutcome? outcome, int? distance = null, int flag = 0) => new()
    {
        QuestionId = Guid.NewGuid().ToString(), QuestionType = "secret type", Question = "secret question",
        GoldAnswer = "secret gold", AgentResponse = "secret response", Correct = outcome == TypedMemEvalOutcome.Correct,
        RawScore = null, ExecutionStatus = QuestionExecutionStatus.Completed,
        TypedOutcome = outcome is null ? null : new()
        {
            Outcome = outcome.Value, Shape = "secret shape", Attribution = TypedMemEvalEvidenceAttribution.Unobserved,
            DistanceSessions = distance, IsStaleRecall = flag == 1, IsOverForgetting = flag == 2, IsMisattributedForgetting = flag == 3,
        },
    };

    [Fact]
    public void Create_DistanceBuckets_RetainsEveryOutcomeDenominatorAndMissingness()
    {
        var questions = Enum.GetValues<TypedMemEvalOutcome>().Select(o => Question(o, 20)).ToList();
        questions.Add(Question(TypedMemEvalOutcome.Correct, 0));
        questions.Add(Question(TypedMemEvalOutcome.Inconclusive));
        questions.Add(Question(null));
        var ext = TypedMemoryDiagnostics.Create(questions, "workingmemory", true);
        var value = ext[TypedMemoryDiagnostics.ExtensionKey]!;
        Assert.Equal(10, (int)value["observedQuestions"]!);
        Assert.Equal(1, (int)value["missingTypedQuestions"]!);
        Assert.Equal(9, (int)value["outcomes"]!["n"]!);
        Assert.Equal(1, (int)value["missingDistanceQuestions"]!);
        var buckets = value["byDistanceSessions"]!.AsObject();
        Assert.Equal(new[] { "0", "20" }, buckets.Select(p => p.Key));
        Assert.Equal(1, (int)buckets["0"]!["n"]!);
        Assert.Equal(7, (int)buckets["20"]!["n"]!);
        foreach (var outcome in Enum.GetValues<TypedMemEvalOutcome>())
            Assert.Equal(1, (int)buckets["20"]![outcome.ToString().ToLowerInvariant()]!);
        Assert.Null(value["forgetting"]);
        Assert.False((bool)value["gating"]!);
        Assert.DoesNotContain("secret", ext.ToJsonString());
    }

    [Fact]
    public void Create_UnknownOutcome_RefusesInsteadOfPublishingInconsistentDenominator()
    {
        Assert.Throws<ArgumentException>(() => TypedMemoryDiagnostics.Create([Question((TypedMemEvalOutcome)999)], "episodic", true));
    }

    [Fact]
    public void Create_ForgettingCounts_RetainsZeroAndExcludesMissingTypedDetails()
    {
        var questions = new[] { Question(TypedMemEvalOutcome.Wrong, flag: 1), Question(TypedMemEvalOutcome.Wrong, flag: 1),
            Question(TypedMemEvalOutcome.Wrong, flag: 2), Question(TypedMemEvalOutcome.Inconclusive), Question(null) };
        var value = TypedMemoryDiagnostics.Create(questions, "forgetting", false)[TypedMemoryDiagnostics.ExtensionKey]!;
        var counts = value["forgetting"]!;
        Assert.Equal(4, (int)counts["n"]!);
        Assert.Equal(2, (int)counts["staleRecall"]!);
        Assert.Equal(1, (int)counts["overForgetting"]!);
        Assert.Equal(0, (int)counts["misattributedForgetting"]!);
        Assert.False((bool)value["complete"]!);
        Assert.Equal("observed-questions", (string?)value["scope"]);
        Assert.Null(value["byDistanceSessions"]);
        Assert.DoesNotContain("secret", value.ToJsonString());
    }

    [Theory]
    [InlineData("workingmemory")]
    [InlineData("forgetting")]
    [InlineData("episodic")]
    public void Create_EmptyInterruptedRun_DeclaresZeroObservedAndNoInventedMeasurements(string vertical)
    {
        var value = TypedMemoryDiagnostics.Create([], vertical, false)[TypedMemoryDiagnostics.ExtensionKey]!;
        Assert.Equal("1.0", (string?)value["schemaVersion"]);
        Assert.False((bool)value["complete"]!);
        Assert.Equal(0, (int)value["outcomes"]!["n"]!);
        Assert.Equal(0, (int)value["observedQuestions"]!);
        if (vertical == "workingmemory") Assert.Empty(value["byDistanceSessions"]!.AsObject());
        if (vertical == "forgetting") Assert.Equal(0, (int)value["forgetting"]!["n"]!);
    }
}
