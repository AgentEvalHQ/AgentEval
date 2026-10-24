// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Metrics.Retrieval;
using AgentEval.Samples;
using Xunit;

namespace AgentEval.Tests.Samples;

public sealed class ComprehensiveRagDataTests
{
    [Fact]
    public void GroundTruth_NamedFixtureFacts_AreIncludedInRelevantSources()
    {
        var documents = ComprehensiveRagExampleData.KnowledgeBase.ToDictionary(d => d.Id, d => d.Text);
        Assert.Equal(ComprehensiveRagExampleData.RelevantDocumentIds.Count,
            ComprehensiveRagExampleData.RelevantDocumentIds.Distinct().Count());
        Assert.All(ComprehensiveRagExampleData.RelevantDocumentIds, id => Assert.True(documents.ContainsKey(id)));
        Assert.All(ComprehensiveRagExampleData.RelevantDocumentIds, id => Assert.Contains("Paris", documents[id]));
        (string Id, string Entity)[] facts =
        [
            ("doc2", "Paris"), ("doc3", "Eiffel Tower"),
            ("doc4", "Louvre Museum"), ("doc8", "Palace of Versailles")
        ];

        foreach (var (id, entity) in facts)
        {
            Assert.Contains(entity, documents[id]);
            if (ComprehensiveRagExampleData.GroundTruth.Contains(entity, StringComparison.Ordinal))
                Assert.Contains(id, ComprehensiveRagExampleData.RelevantDocumentIds);
        }
    }

    [Theory]
    [InlineData("doc2", "Paris is the capital")]
    [InlineData("doc3", "Eiffel Tower")]
    [InlineData("doc4", "Louvre Museum")]
    public void GroundTruth_CapitalAndCityLandmarks_AgreeWithSourceFacts(string id, string fact)
    {
        var source = ComprehensiveRagExampleData.KnowledgeBase.Single(d => d.Id == id).Text;
        Assert.Contains("Paris", source);
        Assert.Contains(fact, source);
        Assert.Contains(fact, ComprehensiveRagExampleData.GroundTruth);
        Assert.Contains(id, ComprehensiveRagExampleData.RelevantDocumentIds);
    }

    [Fact]
    public void Query_CapitalCity_HasExplicitCityScope()
    {
        Assert.Contains("capital of France", ComprehensiveRagExampleData.Query);
        Assert.Contains("in that city", ComprehensiveRagExampleData.Query);
    }

    [Theory]
    [InlineData(false, 100.0)]
    [InlineData(true, 200.0 / 3)]
    public async Task Retrieval_ReferenceVersusDistractor_RecordsActualCoverage(bool includeDistractor, double expected)
    {
        var context = new EvaluationContext
        {
            Input = ComprehensiveRagExampleData.Query,
            Output = ComprehensiveRagExampleData.GroundTruth,
            GroundTruth = ComprehensiveRagExampleData.GroundTruth
        };
        context.SetProperty("RelevantDocumentIds", ComprehensiveRagExampleData.RelevantDocumentIds);
        context.SetProperty("RetrievedDocumentIds", includeDistractor
            ? new[] { "doc2", "doc3", "doc8" }
            : ComprehensiveRagExampleData.RelevantDocumentIds.ToArray());

        var recall = await new RecallAtKMetric(k: 3).EvaluateAsync(context);
        var mrr = await new MRRMetric().EvaluateAsync(context);

        Assert.Equal(expected, recall.Score, 8);
        Assert.Equal(100, mrr.Score);
    }
}
