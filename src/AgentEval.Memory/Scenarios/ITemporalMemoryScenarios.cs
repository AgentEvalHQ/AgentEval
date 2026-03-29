using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Interface for creating temporal memory test scenarios.
/// Factory for complex time-based memory evaluation patterns.
/// </summary>
public interface ITemporalMemoryScenarios
{
    /// <summary>
    /// Creates a scenario testing memory of events at specific time points.
    /// </summary>
    /// <param name="timePoints">Specific timestamps to test memory for</param>
    /// <param name="eventsPerTimepoint">Number of events to remember per timestamp</param>
    /// <returns>Time-point memory test scenario</returns>
    MemoryTestScenario CreateTimePointMemoryTest(
        IEnumerable<DateTimeOffset> timePoints,
        int eventsPerTimepoint = 3);

    /// <summary>
    /// Creates a scenario testing memory of temporal sequences and ordering.
    /// </summary>
    /// <param name="events">Sequence of events with timestamps</param>
    /// <param name="shuffleQueries">Whether to ask about events in non-chronological order</param>
    /// <returns>Temporal sequence memory test scenario</returns>
    MemoryTestScenario CreateSequenceMemoryTest(
        IEnumerable<MemoryFact> events,
        bool shuffleQueries = true);

    /// <summary>
    /// Creates a scenario testing causal reasoning about temporal relationships.
    /// </summary>
    /// <param name="causalChains">Sequences of causally related events</param>
    /// <returns>Causal reasoning memory test scenario</returns>
    MemoryTestScenario CreateCausalReasoningTest(IEnumerable<IEnumerable<MemoryFact>> causalChains);

    /// <summary>
    /// Creates a scenario with overlapping time windows and competing facts.
    /// </summary>
    /// <param name="overlappingFacts">Facts with overlapping time ranges</param>
    /// <param name="timeWindow">The time window to test within</param>
    /// <returns>Overlapping time window memory test scenario</returns>
    MemoryTestScenario CreateOverlappingTimeWindowTest(
        IEnumerable<MemoryFact> overlappingFacts,
        (DateTimeOffset start, DateTimeOffset end) timeWindow);

    /// <summary>
    /// Creates a scenario testing memory degradation over time with different fact ages.
    /// </summary>
    /// <param name="facts">Facts with different timestamps and importance levels</param>
    /// <returns>Memory degradation test scenario</returns>
    MemoryTestScenario CreateMemoryDegradationTest(IEnumerable<MemoryFact> facts);
}
