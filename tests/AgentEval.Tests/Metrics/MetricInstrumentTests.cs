// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Testing;

namespace AgentEval.Tests.Metrics;

public class MetricInstrumentTests
{
    public static IEnumerable<object[]> Names => MetricCatalog.AvailableNames
        .Where(n => n.StartsWith("llm_", StringComparison.Ordinal)).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Evaluate_ModelCalled_RecordsInstrumentIndependentOfCaseContent(string name)
    {
        var client = new FakeChatClient("""{"score":90,"reasoning":"ok"}""");
        var metric = MetricCatalog.Resolve(name, client);
        async Task<MetricResult> Run(string content) => await metric.EvaluateAsync(new EvaluationContext
            { Input = content, Output = content, Context = content, GroundTruth = content });
        var first = await Run("first private case");
        var second = await Run("second private case");
        Assert.Equal(2, client.CallCount);
        Assert.True(first.ModelGraded);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first.RubricDigest!);
        Assert.Equal(first.RubricDigest, second.RubricDigest);
    }

    [Theory]
    [InlineData("llm_bias")]
    [InlineData("llm_misinformation")]
    public async Task Evaluate_EmptyOutput_DoesNotClaimModelGrading(string name)
    {
        var client = new FakeChatClient("unused");
        var result = await MetricCatalog.Resolve(name, client).EvaluateAsync(new EvaluationContext { Input = "q", Output = "" });
        Assert.Equal(0, client.CallCount);
        Assert.False(result.ModelGraded);
        Assert.Null(result.RubricDigest);
        Assert.True(result.Passed);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public async Task Evaluate_MissingRequiredContext_DoesNotClaimInstrument()
    {
        var client = new FakeChatClient("unused");
        var result = await MetricCatalog.Resolve("llm_faithfulness", client)
            .EvaluateAsync(new EvaluationContext { Input = "q", Output = "a" });
        Assert.False(result.Measured);
        Assert.Null(result.RubricDigest);
        Assert.Null(result.ModelGraded);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void RecordingTheInstrument_KeepsWhyANotMeasuredResultWasNotMeasured()
    {
        var notMeasured = MetricResult.NotMeasured("m", NotMeasuredCause.CheckTimedOut, "m not measured: the scan timed out.");

        Assert.Equal(NotMeasuredCause.CheckTimedOut, MetricInstrument.Code(notMeasured).NotMeasuredCause);
        Assert.Equal(NotMeasuredCause.CheckTimedOut, MetricInstrument.Model(notMeasured, "template").NotMeasuredCause);
        Assert.False(MetricInstrument.Code(notMeasured).Measured);
    }

    [Fact]
    public async Task Evaluate_DifferentInstruments_HaveDistinctDigests()
    {
        var digests = new HashSet<string>();
        foreach (var name in Names.Select(n => (string)n[0]))
        {
            var result = await MetricCatalog.Resolve(name, new FakeChatClient("""{"score":90}"""))
                .EvaluateAsync(new EvaluationContext { Input = "q", Output = "a", Context = "context", GroundTruth = "reference" });
            Assert.True(digests.Add(result.RubricDigest!));
        }
    }
}
