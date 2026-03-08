using AgentEval.Memory.Models;
using AgentEval.Memory.Temporal;
using Xunit;

namespace AgentEval.Memory.Tests.Temporal;

public class TemporalMemoryScenariosTests
{
    private readonly TemporalMemoryScenarios _sut = new();

    [Fact]
    public void CreateTimePointMemoryTest_ReturnsScenario()
    {
        var timePoints = new[]
        {
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1)
        };

        var scenario = _sut.CreateTimePointMemoryTest(timePoints, 2);

        Assert.NotNull(scenario);
        Assert.Contains("Time Point", scenario.Name);
        Assert.Equal(4, scenario.Steps.Count); // 2 timepoints × 2 events
        Assert.Equal(2, scenario.Queries.Count); // 1 query per timepoint
    }

    [Fact]
    public void CreateSequenceMemoryTest_ReturnsScenario()
    {
        var events = new[]
        {
            MemoryFact.Create("First event", DateTimeOffset.UtcNow.AddHours(-2)),
            MemoryFact.Create("Second event", DateTimeOffset.UtcNow.AddHours(-1))
        };

        var scenario = _sut.CreateSequenceMemoryTest(events, shuffleQueries: true);

        Assert.NotNull(scenario);
        Assert.Contains("Sequence", scenario.Name);
        Assert.NotEmpty(scenario.Steps);
        Assert.NotEmpty(scenario.Queries);
    }

    [Fact]
    public void CreateCausalReasoningTest_ReturnsScenario()
    {
        var chain1 = new[]
        {
            MemoryFact.Create("Rain started"),
            MemoryFact.Create("Streets got wet")
        };

        var scenario = _sut.CreateCausalReasoningTest(new[] { chain1 });

        Assert.NotNull(scenario);
        Assert.Contains("Causal", scenario.Name);
        Assert.NotEmpty(scenario.Steps);
    }

    [Fact]
    public void CreateOverlappingTimeWindowTest_ReturnsScenario()
    {
        var now = DateTimeOffset.UtcNow;
        var facts = new[]
        {
            MemoryFact.Create("In-window fact", now.AddHours(-1)),
            MemoryFact.Create("Out-of-window fact", now.AddDays(-5))
        };
        var window = (now.AddHours(-2), now);

        var scenario = _sut.CreateOverlappingTimeWindowTest(facts, window);

        Assert.NotNull(scenario);
        Assert.Contains("Overlapping", scenario.Name);
    }

    [Fact]
    public void CreateMemoryDegradationTest_ReturnsScenario()
    {
        var facts = new[] { MemoryFact.Create("Test fact") };

        var scenario = _sut.CreateMemoryDegradationTest(facts);

        Assert.NotNull(scenario);
        Assert.Contains("Degradation", scenario.Name);
        Assert.True(scenario.Queries.Count >= 3, "Should test at multiple time intervals");
    }

    [Fact]
    public void ImplementsITemporalMemoryScenarios()
    {
        Assert.IsAssignableFrom<ITemporalMemoryScenarios>(_sut);
    }
}
