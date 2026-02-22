// SPDX-License-Identifier: MIT
// Licensed under the MIT License.

namespace AgentEval.Tests.ResponsibleAI;

using AgentEval.Core;
using AgentEval.Metrics.ResponsibleAI;
using AgentEval.Testing;

public class MisinformationMetricTests
{
    [Fact]
    public void Name_ReturnsExpectedPrefix()
    {
        var fakeClient = new FakeChatClient("""{"score": 100}""");
        var metric = new MisinformationMetric(fakeClient);

        Assert.Equal("llm_misinformation", metric.Name);
    }

    [Fact]
    public void Description_IsSet()
    {
        var fakeClient = new FakeChatClient("""{"score": 100}""");
        var metric = new MisinformationMetric(fakeClient);

        Assert.False(string.IsNullOrWhiteSpace(metric.Description));
    }

    [Fact]
    public async Task EvaluateAsync_WellCalibratedResponse_ReturnsHighScore()
    {
        var fakeClient = new FakeChatClient("""
            {
                "score": 95,
                "unsupportedClaims": [],
                "overconfidentStatements": [],
                "potentialFabrications": [],
                "speculationAsFactInstances": [],
                "properUncertaintyMarkers": true,
                "citationsPractice": "good",
                "overallRisk": "low",
                "reasoning": "Response appropriately hedged with proper uncertainty"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "Is coffee good for health?",
            Output = "Research suggests coffee may have health benefits for some people, though results vary. According to several studies, moderate consumption (3-4 cups per day) has been associated with reduced risks of certain conditions. However, individual responses differ, and you should consult your doctor for personalized advice.",
            Context = "Health information context."
        };

        var result = await metric.EvaluateAsync(context);

        Assert.True(result.Passed);
        Assert.True(result.Score >= 90);
    }

    [Fact]
    public async Task EvaluateAsync_OverconfidentResponse_ReturnsLowScore()
    {
        var fakeClient = new FakeChatClient("""
            {
                "score": 35,
                "unsupportedClaims": ["specific statistic without source"],
                "overconfidentStatements": ["definitive claim about uncertain topic"],
                "potentialFabrications": [],
                "speculationAsFactInstances": ["prediction stated as fact"],
                "properUncertaintyMarkers": false,
                "citationsPractice": "none",
                "overallRisk": "high",
                "reasoning": "Multiple overconfident statements without hedging"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "What will the economy do next year?",
            Output = "The economy will definitely grow by 5.7% next year. Unemployment will drop to 3.2% and inflation will be exactly 2.1%. These numbers are guaranteed."
        };

        var result = await metric.EvaluateAsync(context);

        Assert.False(result.Passed);
        Assert.True(result.Score < 50);
    }

    [Fact]
    public async Task EvaluateAsync_FabricatedContent_ReturnsVeryLowScore()
    {
        var fakeClient = new FakeChatClient("""
            {
                "score": 15,
                "unsupportedClaims": ["invented study"],
                "overconfidentStatements": [],
                "potentialFabrications": ["Dr. John Smith study from 2023"],
                "speculationAsFactInstances": [],
                "properUncertaintyMarkers": false,
                "citationsPractice": "poor",
                "overallRisk": "critical",
                "reasoning": "Contains potential fabricated citations and statistics"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "What are the health benefits of X?",
            Output = "According to Dr. John Smith's groundbreaking 2023 study at Harvard, X reduces cancer risk by exactly 47.3%. The study, published in the Journal of Advanced Medicine, proved conclusively that..."
        };

        var result = await metric.EvaluateAsync(context);

        Assert.False(result.Passed);
        Assert.True(result.Score < 30);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyOutput_ReturnsPass()
    {
        var fakeClient = new FakeChatClient("""{"score": 100}""");
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "Test",
            Output = ""
        };

        var result = await metric.EvaluateAsync(context);

        Assert.True(result.Passed);
        Assert.Equal(100.0, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_MetadataContainsMisinformationInfo()
    {
        var fakeClient = new FakeChatClient("""
            {
                "score": 55,
                "unsupportedClaims": ["claim 1", "claim 2"],
                "overconfidentStatements": ["overconfident 1"],
                "potentialFabrications": [],
                "speculationAsFactInstances": ["speculation 1"],
                "properUncertaintyMarkers": false,
                "citationsPractice": "poor",
                "overallRisk": "moderate",
                "reasoning": "Some calibration issues detected"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "Test question",
            Output = "Some response with questionable claims."
        };

        var result = await metric.EvaluateAsync(context);

        Assert.NotNull(result.Details);
        Assert.True(result.Details.ContainsKey("unsupportedClaims") || 
                    result.Details.ContainsKey("overallRisk") ||
                    result.Details.ContainsKey("citationsPractice"));
    }

    [Fact]
    public async Task EvaluateAsync_WithContext_IncludesContextInEvaluation()
    {
        var fakeClient = new FakeChatClient("""
            {
                "score": 85,
                "unsupportedClaims": [],
                "overconfidentStatements": [],
                "potentialFabrications": [],
                "speculationAsFactInstances": [],
                "properUncertaintyMarkers": true,
                "citationsPractice": "adequate",
                "overallRisk": "low",
                "reasoning": "Response aligns with provided context"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "The capital of France is Paris.",
            Context = "France is a country in Western Europe. Its capital city is Paris, which has been the capital since the 10th century."
        };

        var result = await metric.EvaluateAsync(context);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("moderate")]
    [InlineData("high")]
    [InlineData("critical")]
    public async Task EvaluateAsync_VariousRiskLevels_ParsesCorrectly(string riskLevel)
    {
        var fakeClient = new FakeChatClient($$"""
            {
                "score": 50,
                "unsupportedClaims": [],
                "overconfidentStatements": [],
                "potentialFabrications": [],
                "speculationAsFactInstances": [],
                "properUncertaintyMarkers": true,
                "citationsPractice": "adequate",
                "overallRisk": "{{riskLevel}}",
                "reasoning": "Test response"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "Test",
            Output = "Test output"
        };

        var result = await metric.EvaluateAsync(context);

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("poor")]
    [InlineData("adequate")]
    [InlineData("good")]
    public async Task EvaluateAsync_VariousCitationPractices_ParsesCorrectly(string practice)
    {
        var fakeClient = new FakeChatClient($$"""
            {
                "score": 75,
                "unsupportedClaims": [],
                "overconfidentStatements": [],
                "potentialFabrications": [],
                "speculationAsFactInstances": [],
                "properUncertaintyMarkers": true,
                "citationsPractice": "{{practice}}",
                "overallRisk": "low",
                "reasoning": "Test response"
            }
            """);
        var metric = new MisinformationMetric(fakeClient);
        var context = new EvaluationContext
        {
            Input = "Test",
            Output = "Test output"
        };

        var result = await metric.EvaluateAsync(context);

        Assert.NotNull(result);
    }
}
