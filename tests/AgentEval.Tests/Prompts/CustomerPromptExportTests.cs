// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Prompts;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Prompts;

public sealed class CustomerPromptExportTests
{
    private const string CustomerText = "Customer-specific judging instructions.";
    private const string Reply = """{"overallScore":90,"summary":"Good","improvements":[],"criteriaResults":[{"id":"criterion-1","met":true,"explanation":"Supported"}]}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturePromptDefinition_NoLeafRubric_PreservesFamilyOrCustomerSystemText(bool customer)
    {
        using var client = new FakeChatClient();
        var judge = new ChatClientEvaluator(client, CustomerText, "original-system", preserveSystemPromptOnBinding: customer);
        var leaf = new AtomicLlmEval(judge, "custom", "Custom", "test", "1.0.0", ["Correct"]);
        var definition = leaf.CapturePromptDefinition();
        Assert.StartsWith(CustomerText, definition.SystemText);
        Assert.Equal("original-system", definition.Id);
        Assert.Equal(JudgeResponseContract.CriteriaV1, definition.ResponseContract);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturePromptDefinition_RubricBoundLeaf_PreservesCustomerOverrideButReplacesFamilyFallback(bool customer)
    {
        using var client = new FakeChatClient();
        var judge = new ChatClientEvaluator(client, CustomerText, "original-system", preserveSystemPromptOnBinding: customer);
        var leaf = Assert.IsType<AtomicLlmEval>(new RelevanceEval(judge).DefinitionInner);
        var definition = leaf.CapturePromptDefinition();
        Assert.Equal(customer, definition.SystemText.StartsWith(CustomerText, StringComparison.Ordinal));
        Assert.Equal(JudgeResponseContract.RubricV1, definition.ResponseContract);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_EnglishSelectionWithoutReplacement_PreservesCustomerSystemInstructions()
    {
        using var client = new FakeChatClient(Reply);
        var judge = new ChatClientEvaluator(client, CustomerText, "original-system").WithPrompts(new() { Locale = "en" });
        var result = await judge.EvaluateAsync("Q", "A", ["Correct"]);
        Assert.StartsWith(CustomerText, client.ReceivedMessages.Single().First().Text);
        Assert.Equal("original-system", result.JudgePrompt!.InstrumentId);
        Assert.False(result.EvaluationFailed);
    }

    [Fact]
    public async Task EvaluateAsync_ExplicitReplacement_OverridesCustomerTextAndRetainsNewIdentity()
    {
        using var client = new FakeChatClient(Reply);
        var definition = BuiltInJudgePrompts.ForEvaluator("judge.criteria", ["Correct"]) with
        { Id = "replacement", SystemText = "Explicit replacement instructions." };
        var judge = new ChatClientEvaluator(client, CustomerText, "original-system").WithPrompts(new() { Override = definition });
        var result = await judge.EvaluateAsync("Q", "A", ["Correct"]);
        Assert.Equal(definition.SystemText, client.ReceivedMessages.Single().First().Text);
        Assert.Equal("replacement", result.JudgePrompt!.InstrumentId);
        Assert.DoesNotContain(CustomerText, client.ReceivedMessages.Single().First().Text);
    }
}
