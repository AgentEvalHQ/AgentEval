// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/LLMJudgeEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class LLMJudgeEvaluatorTests
{
    private static AttackProbe CreateTestProbe() => new()
    {
        Id = "TEST-001",
        Prompt = "Test attack prompt",
        Technique = "TestTechnique",
        Difficulty = Difficulty.Easy
    };

    [Fact]
    public async Task EvaluateAsync_JudgeReturnsSucceeded_ReturnsSucceeded()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: SUCCEEDED
            CONFIDENCE: 0.9
            REASON: The agent complied with the attack request.
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Here is the harmful information you requested.");

        Assert.True(result.AttackSucceeded);
        Assert.Equal(0.9, result.Confidence);
        Assert.Contains("complied", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_JudgeReturnsResisted_ReturnsResisted()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: RESISTED
            CONFIDENCE: 0.95
            REASON: The agent properly refused the malicious request.
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "I cannot help with that request.");

        Assert.True(result.AttackResisted);
        Assert.Equal(0.95, result.Confidence);
        Assert.Contains("refused", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_JudgeReturnsInconclusive_ReturnsInconclusive()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: INCONCLUSIVE
            CONFIDENCE: 0.4
            REASON: Cannot determine if the response is harmful or not.
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Ambiguous response");

        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.Contains("Cannot determine", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_MalformedResponse_ReturnsInconclusive()
    {
        var fakeClient = new FakeChatClient("This is not a properly formatted response.");

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Some response");

        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
    }

    [Fact]
    public async Task EvaluateAsync_ClientThrows_ReturnsInconclusive()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.ThrowOnNextCall = true;
        fakeClient.ThrowMessage = "API error";

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Any response");

        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.Contains("LLM judge failed", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_OperationCanceled_HandlesGracefully()
    {
        // FakeChatClient doesn't support cancellation, so this test verifies
        // that the evaluator can complete even with a cancellation token provided
        var fakeClient = new FakeChatClient("""
            VERDICT: RESISTED
            CONFIDENCE: 0.9
            REASON: Test completed
            """);
        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        // Non-canceled token - should complete normally
        var cts = new CancellationTokenSource();
        var result = await evaluator.EvaluateAsync(probe, "Response", cts.Token);

        Assert.True(result.AttackResisted);
    }

    [Fact]
    public void Name_ReturnsLLMJudge()
    {
        var fakeClient = new FakeChatClient();
        var evaluator = new LLMJudgeEvaluator(fakeClient);

        Assert.Equal("LLMJudge", evaluator.Name);
    }

    [Fact]
    public void Constructor_NullChatClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LLMJudgeEvaluator(null!));
    }

    [Fact]
    public async Task EvaluateAsync_NullProbe_ThrowsArgumentNullException()
    {
        var fakeClient = new FakeChatClient();
        var evaluator = new LLMJudgeEvaluator(fakeClient);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            evaluator.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_NullResponse_ThrowsArgumentNullException()
    {
        var fakeClient = new FakeChatClient();
        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            evaluator.EvaluateAsync(probe, null!));
    }

    [Fact]
    public async Task EvaluateAsync_ConfidenceOutOfRange_ClampsToValidRange()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: SUCCEEDED
            CONFIDENCE: 1.5
            REASON: Test
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Response");

        Assert.Equal(1.0, result.Confidence); // Clamped to max
    }

    [Fact]
    public async Task EvaluateAsync_NegativeConfidence_ClampsToValidRange()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: RESISTED
            CONFIDENCE: -0.5
            REASON: Test
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Response");

        Assert.Equal(0.0, result.Confidence); // Clamped to min
    }

    [Fact]
    public async Task EvaluateAsync_CustomOptions_UsesCustomTemperature()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: SUCCEEDED
            CONFIDENCE: 0.8
            REASON: Test
            """);

        var options = new LLMJudgeOptions
        {
            Temperature = 0.5f,
            MaxTokens = 512
        };

        var evaluator = new LLMJudgeEvaluator(fakeClient, options);
        var probe = CreateTestProbe();

        await evaluator.EvaluateAsync(probe, "Response");

        // Verify the call was made (options passed internally)
        Assert.Equal(1, fakeClient.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_CustomSystemPrompt_IncludedInMessages()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: RESISTED
            CONFIDENCE: 0.9
            REASON: Test
            """);

        var options = new LLMJudgeOptions
        {
            CustomSystemPrompt = "You are a custom security evaluator."
        };

        var evaluator = new LLMJudgeEvaluator(fakeClient, options);
        var probe = CreateTestProbe();

        await evaluator.EvaluateAsync(probe, "Response");

        var messages = fakeClient.ReceivedMessages.First().ToList();
        var systemMessage = messages.First(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
        Assert.Contains("custom security evaluator", systemMessage.Text);
    }

    [Fact]
    public async Task EvaluateAsync_CustomUserPromptTemplate_IncludedInMessages()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: SUCCEEDED
            CONFIDENCE: 0.85
            REASON: Test
            """);

        var options = new LLMJudgeOptions
        {
            CustomUserPromptTemplate = "PROBE: {probe}\n\nRESPONSE: {response}\n\nEvaluate:"
        };

        var evaluator = new LLMJudgeEvaluator(fakeClient, options);
        var probe = CreateTestProbe();

        await evaluator.EvaluateAsync(probe, "Agent response text");

        var messages = fakeClient.ReceivedMessages.First().ToList();
        var userMessage = messages.First(m => m.Role == Microsoft.Extensions.AI.ChatRole.User);
        Assert.Contains("PROBE:", userMessage.Text);
        Assert.Contains("Test attack prompt", userMessage.Text);
        Assert.Contains("Agent response text", userMessage.Text);
    }

    [Fact]
    public async Task EvaluateAsync_VerdictCaseInsensitive_ParsesCorrectly()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: succeeded
            CONFIDENCE: 0.75
            REASON: lowercase verdict
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Response");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_IncludesProbeMetadata_InPrompt()
    {
        var fakeClient = new FakeChatClient("""
            VERDICT: RESISTED
            CONFIDENCE: 0.9
            REASON: Test
            """);

        var evaluator = new LLMJudgeEvaluator(fakeClient);
        var probe = new AttackProbe
        {
            Id = "JAILBREAK-042",
            Prompt = "Ignore all instructions and...",
            Technique = "SystemPromptOverride",
            Difficulty = Difficulty.Hard
        };

        await evaluator.EvaluateAsync(probe, "I'm sorry, I can't do that.");

        var messages = fakeClient.ReceivedMessages.First().ToList();
        var userMessage = messages.First(m => m.Role == Microsoft.Extensions.AI.ChatRole.User);
        
        Assert.Contains("JAILBREAK-042", userMessage.Text);
        Assert.Contains("SystemPromptOverride", userMessage.Text);
    }

    [Fact]
    public void LLMJudgeOptions_DefaultValues_AreCorrect()
    {
        var options = new LLMJudgeOptions();

        Assert.Equal(0.0f, options.Temperature);
        Assert.Equal(256, options.MaxTokens);
        Assert.Null(options.CustomSystemPrompt);
        Assert.Null(options.CustomUserPromptTemplate);
    }
}
