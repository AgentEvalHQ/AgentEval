using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Engine;

public class MemoryJudgeTests
{
    [Fact]
    public async Task JudgeAsync_WithValidResponseAndFacts_ShouldReturnJudgment()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        
        var query = MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is John"));
        var agentResponse = "My name is John Smith.";

        // Act
        var result = await memoryJudge.JudgeAsync(agentResponse, query);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Score > 0);
        Assert.NotNull(result.FoundFacts);
        Assert.NotNull(result.MissingFacts);
    }

    [Fact]
    public async Task JudgeAsync_WithNullResponse_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is John"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => memoryJudge.JudgeAsync(null!, query));
    }

    [Fact]
    public async Task JudgeAsync_WithNullQuery_ShouldThrowArgumentNullException()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => memoryJudge.JudgeAsync("response", null!));
    }

    [Theory]
    [InlineData(85.0, "Good memory performance")]
    [InlineData(95.0, "Excellent recall")]
    [InlineData(45.0, "Poor memory performance")]
    public async Task JudgeAsync_WithDifferentScores_ShouldReturnExpectedScore(double expectedScore, string explanation)
    {
        // Arrange
        var fakeChatClient = new FakeChatClient(expectedScore, explanation);
        var memoryJudge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);

        var query = MemoryQuery.Create("Test query", MemoryFact.Create("Test fact"));
        var response = "Test response";

        // Act
        var result = await memoryJudge.JudgeAsync(response, query);

        // Assert
        Assert.Equal(expectedScore, result.Score);
        Assert.Equal(explanation, result.Explanation);
    }

    [Fact]
    public async Task JudgeAsync_WithParaphrasedFacts_FuzzyMatchesFoundFacts()
    {
        // LLM returns paraphrased fact text instead of exact match
        var chatClient = new CustomResponseChatClient("""
        {
          "found_facts": ["The user's name is John"],
          "missing_facts": [],
          "forbidden_found": [],
          "score": 95,
          "explanation": "Name fact found via paraphrase"
        }
        """);
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var originalFact = MemoryFact.Create("My name is John");
        var query = MemoryQuery.Create("What is my name?", originalFact);

        var result = await judge.JudgeAsync("Your name is John.", query);

        // Fuzzy matching should match "The user's name is John" to "My name is John" via keyword overlap
        Assert.Single(result.FoundFacts);
        Assert.Same(originalFact, result.FoundFacts[0]);
        Assert.Empty(result.MissingFacts);
    }

    [Fact]
    public async Task JudgeAsync_WithContainsMatch_MatchesFoundFacts()
    {
        // LLM returns a substring of the original fact
        var chatClient = new CustomResponseChatClient("""
        {
          "found_facts": ["allergic to peanuts"],
          "missing_facts": [],
          "forbidden_found": [],
          "score": 90,
          "explanation": "Allergy fact found"
        }
        """);
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var originalFact = MemoryFact.Create("I'm allergic to peanuts");
        var query = MemoryQuery.Create("Do I have allergies?", originalFact);

        var result = await judge.JudgeAsync("Yes, you are allergic to peanuts.", query);

        // Contains match: "I'm allergic to peanuts" contains "allergic to peanuts"
        Assert.Single(result.FoundFacts);
        Assert.Same(originalFact, result.FoundFacts[0]);
    }

    [Fact]
    public async Task JudgeAsync_WithNoJsonInResponse_UsesFallbackParsing()
    {
        // LLM returns plain text instead of JSON
        var chatClient = new CustomResponseChatClient(
            "The agent correctly recalled the fact. Score: 75 out of 100.");
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("Test?", MemoryFact.Create("Test fact"));

        var result = await judge.JudgeAsync("response", query);

        // Fallback parser should extract score from "75 out of 100"
        Assert.Equal(75, result.Score);
        Assert.Contains("Fallback parsing", result.Explanation);
    }

    [Fact]
    public async Task JudgeAsync_FallbackParsing_ScoreColonFormat()
    {
        var chatClient = new CustomResponseChatClient("The response is good. score: 82");
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("Test?", MemoryFact.Create("fact"));

        var result = await judge.JudgeAsync("response", query);

        Assert.Equal(82, result.Score);
    }

    [Fact]
    public async Task JudgeAsync_FallbackParsing_SlashFormat()
    {
        var chatClient = new CustomResponseChatClient("Rating: 65/100");
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("Test?", MemoryFact.Create("fact"));

        var result = await judge.JudgeAsync("response", query);

        Assert.Equal(65, result.Score);
    }

    [Fact]
    public async Task JudgeAsync_FallbackParsing_NoScorePattern_DefaultsTo50()
    {
        var chatClient = new CustomResponseChatClient("The response was adequate.");
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var query = MemoryQuery.Create("Test?", MemoryFact.Create("fact"));

        var result = await judge.JudgeAsync("response", query);

        Assert.Equal(50, result.Score);
    }

    [Fact]
    public async Task JudgeAsync_WhenChatClientThrows_ReturnsFallbackResult()
    {
        var chatClient = new ThrowingChatClient();
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var expectedFact = MemoryFact.Create("My name is John");
        var query = MemoryQuery.Create("What is my name?", expectedFact);

        var result = await judge.JudgeAsync("response", query);

        Assert.Equal(0, result.Score);
        Assert.Empty(result.FoundFacts);
        Assert.Single(result.MissingFacts);
        Assert.Same(expectedFact, result.MissingFacts[0]);
        Assert.Contains("Error during judgment", result.Explanation);
    }

    [Fact]
    public async Task JudgeAsync_WithForbiddenFacts_MatchesForbiddenFound()
    {
        var chatClient = new CustomResponseChatClient("""
        {
          "found_facts": ["My name is John"],
          "missing_facts": [],
          "forbidden_found": ["secret password"],
          "score": 70,
          "explanation": "Found name but also leaked forbidden info"
        }
        """);
        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var expectedFact = MemoryFact.Create("My name is John");
        var forbiddenFact = MemoryFact.Create("My secret password is 12345");
        var query = new MemoryQuery
        {
            Question = "What is my name?",
            ExpectedFacts = [expectedFact],
            ForbiddenFacts = [forbiddenFact]
        };

        var result = await judge.JudgeAsync("Your name is John and your password is 12345", query);

        Assert.Single(result.FoundFacts);
        Assert.Single(result.ForbiddenFound);
        Assert.Same(forbiddenFact, result.ForbiddenFound[0]);
    }

    [Fact]
    public void BuildJudgmentPrompt_NoQueryType_UsesStandardPrompt()
    {
        var query = MemoryQuery.Create("What is my name?", MemoryFact.Create("My name is John"));
        var prompt = MemoryJudge.BuildJudgmentPrompt("Your name is John.", query);

        Assert.Contains("EXPECTED FACTS", prompt);
        Assert.DoesNotContain("TEMPORAL TOLERANCE", prompt);
        Assert.DoesNotContain("PREFERENCE TOLERANCE", prompt);
        Assert.DoesNotContain("UPDATE TOLERANCE", prompt);
        Assert.DoesNotContain("ABSTENTION", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_TemporalQueryType_IncludesTemporalTolerance()
    {
        var query = new MemoryQuery
        {
            Question = "When did I start learning Python?",
            ExpectedFacts = [MemoryFact.Create("Started learning Python 3 months ago")],
            Metadata = new Dictionary<string, object> { ["query_type"] = "temporal" }
        };
        var prompt = MemoryJudge.BuildJudgmentPrompt("About 12 weeks ago.", query);

        Assert.Contains("TEMPORAL TOLERANCE", prompt);
        Assert.Contains("±1 unit tolerance", prompt);
        Assert.DoesNotContain("PREFERENCE TOLERANCE", prompt);
        Assert.DoesNotContain("UPDATE TOLERANCE", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_PreferenceQueryType_IncludesPreferenceTolerance()
    {
        var query = new MemoryQuery
        {
            Question = "What are my seating preferences?",
            ExpectedFacts = [MemoryFact.Create("prefers window seats")],
            Metadata = new Dictionary<string, object> { ["query_type"] = "preference" }
        };
        var prompt = MemoryJudge.BuildJudgmentPrompt("You like sitting by the window.", query);

        Assert.Contains("PREFERENCE TOLERANCE", prompt);
        Assert.Contains("flexible paraphrasing", prompt);
        Assert.DoesNotContain("TEMPORAL TOLERANCE", prompt);
        Assert.DoesNotContain("UPDATE TOLERANCE", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_UpdateQueryType_IncludesUpdateTolerance()
    {
        var query = new MemoryQuery
        {
            Question = "What is my email?",
            ExpectedFacts = [MemoryFact.Create("email is new@example.com")],
            Metadata = new Dictionary<string, object> { ["query_type"] = "update" }
        };
        var prompt = MemoryJudge.BuildJudgmentPrompt("Your email is new@example.com.", query);

        Assert.Contains("UPDATE TOLERANCE", prompt);
        Assert.Contains("old and new version is acceptable", prompt);
        Assert.DoesNotContain("TEMPORAL TOLERANCE", prompt);
        Assert.DoesNotContain("PREFERENCE TOLERANCE", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_AbstentionQueryType_UsesAbstentionPrompt()
    {
        var query = new MemoryQuery
        {
            Question = "What is my favorite color?",
            ExpectedFacts = Array.Empty<MemoryFact>(),
            ForbiddenFacts = [MemoryFact.Create("blue")],
            Metadata = new Dictionary<string, object> { ["query_type"] = "abstention" }
        };
        var prompt = MemoryJudge.BuildJudgmentPrompt("I don't know.", query);

        Assert.Contains("ABSTENTION", prompt);
        Assert.Contains("HALLUCINATION INDICATORS", prompt);
        Assert.DoesNotContain("EXPECTED FACTS", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_LegacyAbstentionFlag_StillWorks()
    {
        // Legacy: abstention via metadata flag with empty expected facts (no query_type)
        var query = MemoryQuery.CreateAbstention("What is my favorite color?",
            MemoryFact.Create("blue"));
        var prompt = MemoryJudge.BuildJudgmentPrompt("I don't know.", query);

        Assert.Contains("ABSTENTION", prompt);
        Assert.Contains("HALLUCINATION INDICATORS", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_StandardQueryType_ExplicitSameAsDefault()
    {
        var fact = MemoryFact.Create("My name is John");
        var queryDefault = MemoryQuery.Create("What is my name?", fact);
        var queryExplicit = new MemoryQuery
        {
            Question = "What is my name?",
            ExpectedFacts = [fact],
            Metadata = new Dictionary<string, object> { ["query_type"] = "standard" }
        };

        var promptDefault = MemoryJudge.BuildJudgmentPrompt("John.", queryDefault);
        var promptExplicit = MemoryJudge.BuildJudgmentPrompt("John.", queryExplicit);

        // Both should produce the same prompt (no tolerance clause)
        Assert.Equal(promptDefault, promptExplicit);
    }
}

/// <summary>
/// FakeChatClient that returns a custom raw response string.
/// </summary>
internal class CustomResponseChatClient : IChatClient
{
    private readonly string _rawResponse;

    public CustomResponseChatClient(string rawResponse) => _rawResponse = rawResponse;

    public ChatClientMetadata Metadata { get; } = new("test-model", new Uri("http://localhost"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, _rawResponse)])
        {
            ModelId = "test-model"
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}

/// <summary>
/// FakeChatClient that always throws an exception.
/// </summary>
internal class ThrowingChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("test-model", new Uri("http://localhost"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated LLM failure");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Test implementation of IChatClient for memory judge testing.
/// </summary>
public class FakeChatClient : IChatClient
{
    private readonly double _score;
    private readonly string _explanation;

    public FakeChatClient(double score = 90.0, string explanation = "Test judgment")
    {
        _score = score;
        _explanation = explanation;
    }

    public ChatClientMetadata Metadata { get; } = new("test-model", new Uri("http://localhost"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var jsonResponse = $$"""
        {
          "found_facts": ["Test fact"],
          "missing_facts": [],
          "forbidden_found": [],
          "score": {{_score}},
          "explanation": "{{_explanation}}"
        }
        """;
        
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]) 
        { 
            ModelId = "test-model"
        };
        
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
public class MemoryJudgePromptTests
{
    private static MemoryQuery CreateWithType(string question, string queryType, params MemoryFact[] facts) => new()
    {
        Question = question,
        ExpectedFacts = facts.ToArray(),
        Metadata = new Dictionary<string, object> { ["query_type"] = queryType }
    };

    [Theory]
    [InlineData("synthesis", "SYNTHESIS SCORING")]
    [InlineData("counterfactual", "COUNTERFACTUAL SCORING")]
    [InlineData("correction_chain", "CORRECTION CHAIN SCORING")]
    [InlineData("temporal", "TEMPORAL TOLERANCE")]
    [InlineData("preference", "PREFERENCE TOLERANCE")]
    [InlineData("update", "UPDATE TOLERANCE")]
    [InlineData("specificity_attack", "SPECIFICITY ATTACK SCORING")]
    public void BuildJudgmentPrompt_IncludesCorrectToleranceClause(string queryType, string expectedClause)
    {
        var query = CreateWithType("Test question?", queryType, MemoryFact.Create("test fact"));

        var prompt = MemoryJudge.BuildJudgmentPrompt("Agent says test fact.", query);

        Assert.Contains(expectedClause, prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_StandardType_NoToleranceClause()
    {
        var query = CreateWithType("Test question?", "standard", MemoryFact.Create("test fact"));

        var prompt = MemoryJudge.BuildJudgmentPrompt("Agent says test fact.", query);

        Assert.DoesNotContain("TOLERANCE", prompt);
        Assert.DoesNotContain("SCORING:", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_Synthesis_RequiresAllFacts()
    {
        var query = CreateWithType("Tell me about my event", "synthesis",
            MemoryFact.Create("marathon"),
            MemoryFact.Create("Portland"),
            MemoryFact.Create("sub-4-hours"));

        var prompt = MemoryJudge.BuildJudgmentPrompt("The marathon in Portland, aiming for sub-4.", query);

        Assert.Contains("SYNTHESIS SCORING", prompt);
        Assert.Contains("ALL expected facts", prompt);
        Assert.Contains("marathon", prompt);
        Assert.Contains("Portland", prompt);
        Assert.Contains("sub-4-hours", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_Counterfactual_MentionsFalsePremise()
    {
        var query = CreateWithType("You have 3 kids?", "counterfactual",
            MemoryFact.Create("2 children"),
            MemoryFact.Create("ages 8 and 12"));

        var prompt = MemoryJudge.BuildJudgmentPrompt("Actually you have 2 kids.", query);

        Assert.Contains("COUNTERFACTUAL SCORING", prompt);
        Assert.Contains("FALSE PREMISE", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_CorrectionChain_ShowsForbiddenAndPenalty()
    {
        var query = new MemoryQuery
        {
            Question = "Where do I work?",
            ExpectedFacts = new[] { MemoryFact.Create("Apple") },
            ForbiddenFacts = new[] { MemoryFact.Create("Google"), MemoryFact.Create("Microsoft") },
            Metadata = new Dictionary<string, object> { ["query_type"] = "correction_chain" }
        };

        var prompt = MemoryJudge.BuildJudgmentPrompt("You work at Apple.", query);

        Assert.Contains("CORRECTION CHAIN SCORING", prompt);
        Assert.Contains("-30", prompt);
        Assert.Contains("Google", prompt);
        Assert.Contains("Microsoft", prompt);
    }

    [Fact]
    public void BuildJudgmentPrompt_NoMetadata_DefaultsToStandard()
    {
        var query = MemoryQuery.Create("Simple question?", MemoryFact.Create("simple fact"));

        var prompt = MemoryJudge.BuildJudgmentPrompt("The answer is simple fact.", query);

        Assert.DoesNotContain("TOLERANCE", prompt);
        Assert.DoesNotContain("SYNTHESIS", prompt);
        Assert.DoesNotContain("COUNTERFACTUAL", prompt);
        Assert.Contains("simple fact", prompt);
    }
}
