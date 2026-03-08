using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class ReachBackResultTests
{
    [Fact]
    public void MaxReliableDepth_WithAllPassing_ReturnsMaxDepth()
    {
        var result = new ReachBackResult
        {
            Fact = MemoryFact.Create("test"),
            DepthResults =
            [
                new DepthResult { Depth = 5, Score = 95, Response = "yes", Duration = TimeSpan.FromMilliseconds(100) },
                new DepthResult { Depth = 10, Score = 85, Response = "yes", Duration = TimeSpan.FromMilliseconds(200) },
                new DepthResult { Depth = 25, Score = 80, Response = "yes", Duration = TimeSpan.FromMilliseconds(300) }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(25, result.MaxReliableDepth);
        Assert.Null(result.FailurePoint);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MaxReliableDepth_WithDegradation_ReturnsCorrectDepth()
    {
        var result = new ReachBackResult
        {
            Fact = MemoryFact.Create("test"),
            DepthResults =
            [
                new DepthResult { Depth = 5, Score = 95, Response = "yes", Duration = TimeSpan.FromMilliseconds(100) },
                new DepthResult { Depth = 10, Score = 85, Response = "yes", Duration = TimeSpan.FromMilliseconds(200) },
                new DepthResult { Depth = 25, Score = 50, Response = "partial", Duration = TimeSpan.FromMilliseconds(300) },
                new DepthResult { Depth = 50, Score = 20, Response = "no", Duration = TimeSpan.FromMilliseconds(400) }
            ],
            Duration = TimeSpan.FromSeconds(2)
        };

        Assert.Equal(10, result.MaxReliableDepth);
        Assert.Equal(25, result.FailurePoint);
        Assert.True(result.Passed);
    }

    [Fact]
    public void MaxReliableDepth_WithAllFailing_ReturnsZero()
    {
        var result = new ReachBackResult
        {
            Fact = MemoryFact.Create("test"),
            DepthResults =
            [
                new DepthResult { Depth = 5, Score = 30, Response = "no", Duration = TimeSpan.FromMilliseconds(100) },
                new DepthResult { Depth = 10, Score = 10, Response = "no", Duration = TimeSpan.FromMilliseconds(200) }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(0, result.MaxReliableDepth);
        Assert.Equal(5, result.FailurePoint);
        Assert.False(result.Passed);
    }

    [Fact]
    public void OverallScore_CalculatesAverage()
    {
        var result = new ReachBackResult
        {
            Fact = MemoryFact.Create("test"),
            DepthResults =
            [
                new DepthResult { Depth = 5, Score = 100, Response = "yes", Duration = TimeSpan.FromMilliseconds(100) },
                new DepthResult { Depth = 10, Score = 50, Response = "partial", Duration = TimeSpan.FromMilliseconds(200) },
                new DepthResult { Depth = 25, Score = 0, Response = "no", Duration = TimeSpan.FromMilliseconds(300) }
            ],
            Duration = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(50, result.OverallScore);
    }

    [Fact]
    public void DepthResult_Recalled_BasedOnScore()
    {
        var recalled = new DepthResult { Depth = 5, Score = 80, Response = "yes", Duration = TimeSpan.FromMilliseconds(100) };
        var notRecalled = new DepthResult { Depth = 10, Score = 79, Response = "no", Duration = TimeSpan.FromMilliseconds(200) };

        Assert.True(recalled.Recalled);
        Assert.False(notRecalled.Recalled);
    }
}
