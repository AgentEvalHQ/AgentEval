namespace AgentEval.Memory.Models;

/// <summary>
/// Represents a question or prompt designed to test whether an agent remembers specific facts.
/// </summary>
public class MemoryQuery
{
    /// <summary>
    /// The question or prompt to ask the agent.
    /// </summary>
    public required string Question { get; init; }

    /// <summary>
    /// Facts that should be reflected in the agent's response to this query.
    /// </summary>
    public required IReadOnlyList<MemoryFact> ExpectedFacts { get; init; }

    /// <summary>
    /// Facts that should NOT appear in the agent's response (forbidden knowledge).
    /// Useful for testing information isolation or temporal boundaries.
    /// </summary>
    public IReadOnlyList<MemoryFact> ForbiddenFacts { get; init; } = Array.Empty<MemoryFact>();

    /// <summary>
    /// Optional timestamp representing when this query is being asked.
    /// Used for temporal memory evaluation ("What did you know at time T?").
    /// </summary>
    public DateTimeOffset? QueryTime { get; init; }

    /// <summary>
    /// Minimum score (0-100) required to consider this query as passed.
    /// Default is 80 (agent must demonstrate good recall).
    /// </summary>
    public int MinimumScore { get; init; } = 80;

    /// <summary>
    /// Optional category this query tests.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Optional metadata for additional context or test configuration.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    public override string ToString() => Question;

    /// <summary>
    /// Creates a simple query with a question and expected facts.
    /// </summary>
    public static MemoryQuery Create(string question, params MemoryFact[] expectedFacts) => new()
    {
        Question = question,
        ExpectedFacts = expectedFacts.ToArray()
    };

    /// <summary>
    /// Creates a query with both expected and forbidden facts.
    /// </summary>
    public static MemoryQuery Create(string question, IReadOnlyList<MemoryFact> expectedFacts, IReadOnlyList<MemoryFact> forbiddenFacts) => new()
    {
        Question = question,
        ExpectedFacts = expectedFacts,
        ForbiddenFacts = forbiddenFacts
    };

    /// <summary>
    /// Creates a temporal query with a specific query time.
    /// </summary>
    public static MemoryQuery CreateTemporal(string question, DateTimeOffset queryTime, params MemoryFact[] expectedFacts) => new()
    {
        Question = question,
        ExpectedFacts = expectedFacts.ToArray(),
        QueryTime = queryTime
    };
}