using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Models;

public class MemoryFactTests
{
    [Fact]
    public void Create_WithValidContent_ShouldCreateFact()
    {
        // Arrange
        var content = "My name is Alice";
        
        // Act
        var fact = new MemoryFact { Content = content };
        
        // Assert
        Assert.NotNull(fact);
        Assert.Equal(content, fact.Content);
        Assert.Null(fact.Category);
        Assert.Null(fact.Timestamp);
        Assert.Equal(50, fact.Importance); // Default value
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyOrWhitespaceContent_ShouldCreateFact(string content)
    {
        // Act - The required keyword prevents null, but allows empty/whitespace
        var fact = new MemoryFact { Content = content };
        
        // Assert
        Assert.NotNull(fact);
        Assert.Equal(content, fact.Content);
    }

    [Fact]
    public void CreateWithCategory_WithValidParameters_ShouldCreateFactWithCategory()
    {
        // Arrange
        var content = "I work as a developer";
        var category = "professional information";
        
        // Act
        var fact = new MemoryFact { Content = content, Category = category };
        
        // Assert
        Assert.Equal(content, fact.Content);
        Assert.Equal(category, fact.Category);
    }

    [Fact]
    public void CreateWithImportance_WithValidParameters_ShouldCreateFactWithImportance()
    {
        // Arrange
        var content = "Critical security information";
        var importance = 95;
        
        // Act
        var fact = new MemoryFact { Content = content, Importance = importance };
        
        // Assert
        Assert.Equal(content, fact.Content);
        Assert.Equal(importance, fact.Importance);
    }

    [Fact]
    public void CreateWithTimestamp_WithValidParameters_ShouldCreateFactWithTimestamp()
    {
        // Arrange
        var content = "Meeting scheduled";
        var timestamp = DateTimeOffset.Now;
        
        // Act
        var fact = new MemoryFact { Content = content, Timestamp = timestamp };
        
        // Assert
        Assert.Equal(content, fact.Content);
        Assert.Equal(timestamp, fact.Timestamp);
    }
}

public class MemoryQueryTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldCreateMemoryQuery()
    {
        // Arrange
        var question = "What is my name?";
        var expectedFact = new MemoryFact { Content = "My name is Alice" };
        
        // Act
        var query = new MemoryQuery 
        { 
            Question = question, 
            ExpectedFacts = [expectedFact] 
        };
        
        // Assert
        Assert.NotNull(query);
        Assert.Equal(question, query.Question);
        Assert.Single(query.ExpectedFacts);
        Assert.Equal(expectedFact, query.ExpectedFacts.First());
        Assert.Empty(query.ForbiddenFacts);
        Assert.Equal(80, query.MinimumScore);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyOrWhitespaceQuestion_ShouldCreateQuery(string question)
    {
        // Arrange
        var fact = new MemoryFact { Content = "Valid fact" };
        
        // Act
        var query = new MemoryQuery 
        { 
            Question = question, 
            ExpectedFacts = [fact] 
        };
        
        // Assert
        Assert.NotNull(query);
        Assert.Equal(question, query.Question);
    }

    [Fact]
    public void CreateWithForbiddenFacts_WithValidParameters_ShouldCreateQueryWithForbiddenFacts()
    {
        // Arrange
        var question = "What is my profession?";
        var expectedFact = new MemoryFact { Content = "I work as a developer" };
        var forbiddenFact = new MemoryFact { Content = "I work as a lawyer" };
        
        // Act
        var query = new MemoryQuery 
        { 
            Question = question, 
            ExpectedFacts = [expectedFact],
            ForbiddenFacts = [forbiddenFact]
        };
        
        // Assert
        Assert.Equal(question, query.Question);
        Assert.Single(query.ExpectedFacts);
        Assert.Single(query.ForbiddenFacts);
        Assert.Equal(forbiddenFact, query.ForbiddenFacts.First());
    }

    [Fact]
    public void CreateWithCustomScore_WithValidParameters_ShouldCreateQueryWithCustomScore()
    {
        // Arrange
        var question = "What is critical information?";
        var expectedFact = new MemoryFact { Content = "Critical data", Importance = 95 };
        var minimumScore = 95;
        
        // Act
        var query = new MemoryQuery 
        { 
            Question = question, 
            ExpectedFacts = [expectedFact],
            MinimumScore = minimumScore
        };
        
        // Assert
        Assert.Equal(question, query.Question);
        Assert.Equal(minimumScore, query.MinimumScore);
    }
}

public class MemoryQueryResultTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldCreateMemoryQueryResult()
    {
        // Arrange
        var query = new MemoryQuery 
        { 
            Question = "What is my name?", 
            ExpectedFacts = [new MemoryFact { Content = "My name is Alice" }],
            MinimumScore = 80
        };
        var response = "Your name is Alice";
        var score = 95.0;
        var foundFacts = new[] { new MemoryFact { Content = "My name is Alice" } };
        var missingFacts = Array.Empty<MemoryFact>();
        var forbiddenFound = Array.Empty<MemoryFact>();
        
        // Act
        var result = new MemoryQueryResult 
        { 
            Query = query, 
            Response = response,
            Score = score,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound
        };
        
        // Assert
        Assert.Equal(query, result.Query);
        Assert.Equal(response, result.Response);
        Assert.Equal(score, result.Score);
        Assert.True(result.Passed); // Score 95 >= MinimumScore 80
        Assert.Equal(foundFacts, result.FoundFacts);
        Assert.Empty(result.MissingFacts);
        Assert.Empty(result.ForbiddenFound);
    }

    [Fact]
    public void Create_WithFailedTest_ShouldCreateFailedResult()
    {
        // Arrange
        var query = new MemoryQuery 
        { 
            Question = "What is my age?", 
            ExpectedFacts = [new MemoryFact { Content = "I am 25 years old" }],
            MinimumScore = 80
        };
        var response = "I don't know your age";
        var score = 30.0;
        var foundFacts = Array.Empty<MemoryFact>();
        var missingFacts = new[] { new MemoryFact { Content = "I am 25 years old" } };
        var forbiddenFound = Array.Empty<MemoryFact>();
        
        // Act
        var result = new MemoryQueryResult 
        { 
            Query = query, 
            Response = response,
            Score = score,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound
        };
        
        // Assert
        Assert.Equal(query, result.Query);
        Assert.False(result.Passed); // Score 30 < MinimumScore 80
        Assert.Equal(score, result.Score);
        Assert.Empty(result.FoundFacts);
        Assert.Single(result.MissingFacts);
    }

    [Fact]
    public void ToString_WithValidResult_ShouldReturnFormattedString()
    {
        // Arrange
        var query = new MemoryQuery 
        { 
            Question = "What is my name?", 
            ExpectedFacts = [new MemoryFact { Content = "My name is Alice" }],
            MinimumScore = 80
        };
        var result = new MemoryQueryResult 
        { 
            Query = query, 
            Response = "Your name is Alice",
            Score = 95.5,
            FoundFacts = Array.Empty<MemoryFact>(),
            MissingFacts = Array.Empty<MemoryFact>(),
            ForbiddenFound = Array.Empty<MemoryFact>()
        };
        
        // Act
        var stringResult = result.ToString();
        
        // Assert
        Assert.Contains("What is my name?", stringResult);
        Assert.Contains("95.5%", stringResult);
        Assert.Contains("PASS", stringResult);
    }
}

public class MemoryEvaluationResultTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldCreateMemoryEvaluationResult()
    {
        // Arrange
        var query = new MemoryQuery 
        { 
            Question = "What is my name?", 
            ExpectedFacts = [new MemoryFact { Content = "My name is Alice" }]
        };
        var queryResult = new MemoryQueryResult 
        { 
            Query = query, 
            Response = "Your name is Alice",
            Score = 95.0,
            FoundFacts = [new MemoryFact { Content = "My name is Alice" }],
            MissingFacts = Array.Empty<MemoryFact>(),
            ForbiddenFound = Array.Empty<MemoryFact>()
        };
        var overallScore = 95.0;
        var foundFacts = new[] { new MemoryFact { Content = "My name is Alice" } };
        var missingFacts = Array.Empty<MemoryFact>();
        var forbiddenFound = Array.Empty<MemoryFact>();
        var duration = TimeSpan.FromSeconds(2);
        
        // Act
        var result = new MemoryEvaluationResult 
        { 
            OverallScore = overallScore,
            QueryResults = [queryResult],
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound,
            Duration = duration,
            ScenarioName = "Test Scenario"
        };
        
        // Assert
        Assert.Equal(overallScore, result.OverallScore);
        Assert.Single(result.QueryResults);
        Assert.Equal(queryResult, result.QueryResults.First());
        Assert.Equal(foundFacts, result.FoundFacts);
        Assert.Empty(result.MissingFacts);
        Assert.Empty(result.ForbiddenFound);
        Assert.Equal(duration, result.Duration);
    }
}