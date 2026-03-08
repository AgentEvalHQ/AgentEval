using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class CrossSessionResultTests
{
    [Fact]
    public void RetainedCount_WithAllRecalled_ReturnsTotal()
    {
        var result = new CrossSessionResult
        {
            ScenarioName = "test",
            Passed = true,
            OverallScore = 100,
            SessionResetSupported = true,
            SessionResetCount = 1,
            Duration = TimeSpan.FromSeconds(2),
            FactResults =
            [
                new CrossSessionFactResult { Fact = "A", Query = "A?", Response = "A", Recalled = true, Score = 95 },
                new CrossSessionFactResult { Fact = "B", Query = "B?", Response = "B", Recalled = true, Score = 90 }
            ]
        };

        Assert.Equal(2, result.RetainedCount);
        Assert.Equal(0, result.LostCount);
    }

    [Fact]
    public void LostCount_WithPartialRecall_CalculatesCorrectly()
    {
        var result = new CrossSessionResult
        {
            ScenarioName = "test",
            Passed = false,
            OverallScore = 50,
            SessionResetSupported = true,
            SessionResetCount = 1,
            Duration = TimeSpan.FromSeconds(2),
            FactResults =
            [
                new CrossSessionFactResult { Fact = "A", Query = "A?", Response = "A", Recalled = true, Score = 90 },
                new CrossSessionFactResult { Fact = "B", Query = "B?", Response = "?", Recalled = false, Score = 20 },
                new CrossSessionFactResult { Fact = "C", Query = "C?", Response = "C", Recalled = true, Score = 85 },
                new CrossSessionFactResult { Fact = "D", Query = "D?", Response = "?", Recalled = false, Score = 10 }
            ]
        };

        Assert.Equal(2, result.RetainedCount);
        Assert.Equal(2, result.LostCount);
    }

    [Fact]
    public void SessionResetNotSupported_HasErrorMessage()
    {
        var result = new CrossSessionResult
        {
            ScenarioName = "test",
            Passed = false,
            OverallScore = 0,
            SessionResetSupported = false,
            Duration = TimeSpan.Zero,
            ErrorMessage = "Agent does not implement ISessionResettableAgent",
            FactResults = []
        };

        Assert.False(result.SessionResetSupported);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, result.RetainedCount);
        Assert.Equal(0, result.LostCount);
    }

    [Fact]
    public void AllLost_RetainedCountZero()
    {
        var result = new CrossSessionResult
        {
            ScenarioName = "test",
            Passed = false,
            OverallScore = 0,
            SessionResetSupported = true,
            SessionResetCount = 1,
            Duration = TimeSpan.FromSeconds(1),
            FactResults =
            [
                new CrossSessionFactResult { Fact = "A", Query = "A?", Response = "?", Recalled = false, Score = 0 },
                new CrossSessionFactResult { Fact = "B", Query = "B?", Response = "?", Recalled = false, Score = 5 }
            ]
        };

        Assert.Equal(0, result.RetainedCount);
        Assert.Equal(2, result.LostCount);
    }
}
