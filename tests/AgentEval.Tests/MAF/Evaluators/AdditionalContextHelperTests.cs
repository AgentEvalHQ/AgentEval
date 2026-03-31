// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Evaluators;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;

namespace AgentEval.Tests.MAF.Evaluators;

public class AdditionalContextHelperTests
{
    [Fact]
    public void ExtractRAGContext_WithRAGContext_ReturnsRetrievedContext()
    {
        var contexts = new MEAIEvaluationContext[]
        {
            new AgentEvalRAGContext("The capital of France is Paris."),
        };

        var result = AdditionalContextHelper.ExtractRAGContext(contexts);

        Assert.Equal("The capital of France is Paris.", result);
    }

    [Fact]
    public void ExtractRAGContext_WithNoRAGContext_ReturnsNull()
    {
        var contexts = new MEAIEvaluationContext[]
        {
            new AgentEvalGroundTruthContext("expected answer"),
        };

        var result = AdditionalContextHelper.ExtractRAGContext(contexts);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractRAGContext_WithNull_ReturnsNull()
    {
        Assert.Null(AdditionalContextHelper.ExtractRAGContext(null));
    }

    [Fact]
    public void ExtractGroundTruth_WithGroundTruthContext_ReturnsGroundTruth()
    {
        var contexts = new MEAIEvaluationContext[]
        {
            new AgentEvalGroundTruthContext("Paris is the capital of France"),
        };

        var result = AdditionalContextHelper.ExtractGroundTruth(contexts);

        Assert.Equal("Paris is the capital of France", result);
    }

    [Fact]
    public void ExtractGroundTruth_WithNull_ReturnsNull()
    {
        Assert.Null(AdditionalContextHelper.ExtractGroundTruth(null));
    }

    [Fact]
    public void ExtractExpectedTools_WithExpectedToolsContext_ReturnsToolNames()
    {
        var contexts = new MEAIEvaluationContext[]
        {
            new AgentEvalExpectedToolsContext(["SearchFlights", "BookHotel"]),
        };

        var result = AdditionalContextHelper.ExtractExpectedTools(contexts);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("SearchFlights", result);
        Assert.Contains("BookHotel", result);
    }

    [Fact]
    public void ExtractExpectedTools_WithNull_ReturnsNull()
    {
        Assert.Null(AdditionalContextHelper.ExtractExpectedTools(null));
    }

    [Fact]
    public void ExtractFromMixedContexts_ReturnsCorrectTypes()
    {
        var contexts = new MEAIEvaluationContext[]
        {
            new AgentEvalRAGContext("context docs"),
            new AgentEvalGroundTruthContext("expected answer"),
            new AgentEvalExpectedToolsContext(["Tool1"]),
        };

        Assert.Equal("context docs", AdditionalContextHelper.ExtractRAGContext(contexts));
        Assert.Equal("expected answer", AdditionalContextHelper.ExtractGroundTruth(contexts));
        Assert.Single(AdditionalContextHelper.ExtractExpectedTools(contexts)!);
    }

    [Fact]
    public void CustomContextTypes_ConstructCorrectly()
    {
        var rag = new AgentEvalRAGContext("docs");
        Assert.Equal("docs", rag.RetrievedContext);

        var gt = new AgentEvalGroundTruthContext("truth");
        Assert.Equal("truth", gt.GroundTruth);

        var tools = new AgentEvalExpectedToolsContext(["a", "b"]);
        Assert.Equal(2, tools.ExpectedToolNames.Count);
    }
}
