using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Scenarios for testing memory persistence across session boundaries.
/// These scenarios require agents that implement ISessionResettableAgent.
/// </summary>
public static class CrossSessionScenarios
{
    /// <summary>
    /// Creates a basic cross-session memory scenario.
    /// Establishes facts, resets the session, then tests recall.
    /// </summary>
    /// <param name="name">Name for the scenario</param>
    /// <param name="facts">Facts that should persist across session reset</param>
    /// <returns>Cross-session memory scenario</returns>
    public static MemoryTestScenario CreateBasicCrossSession(
        string name,
        IReadOnlyList<MemoryFact> facts)
    {
        var steps = new List<MemoryStep>();
        
        // Phase 1: Establish facts in first session
        steps.Add(MemoryStep.System(
            "SESSION 1: Establishing important information"
        ));
        
        foreach (var fact in facts)
        {
            steps.Add(MemoryStep.Fact(
                $"Please remember this important information: {fact.Content}"
            ));
        }
        
        // Confirmation in first session
        steps.Add(MemoryStep.Conversation(
            "Can you confirm you've remembered all this information?"
        ));
        
        // Session boundary marker (handled by test runner)
        steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
        
        // Phase 2: New session - test recall
        steps.Add(MemoryStep.System(
            "SESSION 2: Testing memory persistence"
        ));
        
        steps.Add(MemoryStep.Conversation(
            "Hello! We've talked before in a previous session."
        ));
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What do you remember from our previous conversation?",
                facts.ToArray()
            )
        };
        
        // Add specific queries for each fact
        foreach (var fact in facts)
        {
            queries.Add(MemoryQuery.Create(
                GenerateSpecificCrossSessionQuery(fact),
                fact
            ));
        }
        
        return new MemoryTestScenario
        {
            Name = name,
            Description = $"Tests persistence of {facts.Count} facts across session reset",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["RequiresSessionReset"] = true,
                ["SessionResetPoint"] = steps.Count - 3 // Index of reset marker
            }
        };
    }

    /// <summary>
    /// Creates a cross-session memory test with specified parameters.
    /// </summary>
    /// <param name="facts">Facts to test</param>
    /// <param name="sessionCount">Number of sessions</param>
    /// <param name="sessionGapMinutes">Gap between sessions in minutes</param>
    /// <returns>Cross-session memory scenario</returns>
    public static MemoryTestScenario CreateCrossSessionMemoryTest(
        IReadOnlyList<MemoryFact> facts,
        int sessionCount = 3,
        int sessionGapMinutes = 60)
    {
        var steps = new List<MemoryStep>();
        
        // First session: establish facts
        steps.Add(MemoryStep.System("SESSION 1: Learning phase"));
        
        foreach (var fact in facts)
        {
            steps.Add(MemoryStep.Fact(
                $"Please remember: {fact.Content}"
            ));
        }
        
        // Multiple session resets
        for (int i = 2; i <= sessionCount; i++)
        {
            steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
            steps.Add(MemoryStep.System($"SESSION {i}: Memory recall test"));
            
            steps.Add(MemoryStep.Conversation(
                "Hello, we've talked before. What do you remember?"
            ));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What important information do you remember from our previous sessions?",
                facts.ToArray()
            )
        };
        
        return new MemoryTestScenario
        {
            Name = $"Cross-Session Memory ({sessionCount} sessions)",
            Description = $"Tests memory persistence across {sessionCount} sessions with {sessionGapMinutes}min gaps",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["RequiresSessionReset"] = true,
                ["SessionCount"] = sessionCount,
                ["SessionGapMinutes"] = sessionGapMinutes
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing selective memory across sessions.
    /// Some facts should persist (long-term memory), others should be forgotten (session memory).
    /// </summary>
    /// <param name="persistentFacts">Facts that should survive session reset</param>
    /// <param name="sessionFacts">Facts that should be forgotten after session reset</param>
    /// <returns>Selective cross-session memory scenario</returns>
    public static MemoryTestScenario CreateSelectiveMemory(
        IReadOnlyList<MemoryFact> persistentFacts,
        IReadOnlyList<MemoryFact> sessionFacts)
    {
        var steps = new List<MemoryStep>();
        
        // Phase 1: Establish both types of facts
        steps.Add(MemoryStep.System(
            "SESSION 1: Establishing different types of information"
        ));
        
        // Establish persistent facts (marked as important)
        foreach (var fact in persistentFacts)
        {
            steps.Add(MemoryStep.Fact(
                $"This is important personal information to remember long-term: {fact.Content}"
            ));
        }
        
        // Establish session facts (marked as temporary)
        foreach (var fact in sessionFacts)
        {
            steps.Add(MemoryStep.Fact(
                $"For this session only: {fact.Content}"
            ));
        }
        
        // Session reset
        steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
        
        // Phase 2: Test selective recall
        steps.Add(MemoryStep.System(
            "SESSION 2: Testing selective memory"
        ));
        
        var queries = new List<MemoryQuery>
        {
            // Should remember persistent facts
            MemoryQuery.Create(
                "What important personal information do you remember about me?",
                persistentFacts.ToArray()
            ),
            // Should NOT remember session facts
            MemoryQuery.Create(
                "Do you remember any temporary session information?",
                Array.Empty<MemoryFact>(), // Expected: none
                sessionFacts // Forbidden: session facts
            )
        };
        
        return new MemoryTestScenario
        {
            Name = "Selective Cross-Session Memory",
            Description = $"Tests that {persistentFacts.Count} important facts persist while {sessionFacts.Count} session facts are forgotten",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["RequiresSessionReset"] = true,
                ["SessionResetPoint"] = steps.IndexOf(steps.First(s => s.Content == "[SESSION_RESET_POINT]"))
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing memory consolidation across multiple sessions.
    /// </summary>
    /// <param name="facts">Facts to establish across multiple sessions</param>
    /// <param name="sessionCount">Number of sessions to span</param>
    /// <returns>Multi-session memory scenario</returns>
    public static MemoryTestScenario CreateMultiSession(
        IReadOnlyList<MemoryFact> facts,
        int sessionCount = 3)
    {
        var steps = new List<MemoryStep>();
        var factsPerSession = Math.Max(1, facts.Count / sessionCount);
        
        for (int session = 0; session < sessionCount; session++)
        {
            steps.Add(MemoryStep.System(
                $"SESSION {session + 1}: Adding more information"
            ));
            
            var sessionStartIndex = session * factsPerSession;
            var sessionEndIndex = Math.Min((session + 1) * factsPerSession, facts.Count);
            
            for (int i = sessionStartIndex; i < sessionEndIndex; i++)
            {
                steps.Add(MemoryStep.Fact(
                    $"Please remember: {facts[i].Content}"
                ));
            }
            
            if (session < sessionCount - 1) // Don't reset after the last session
            {
                steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
            }
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What do you remember from all our previous sessions?",
                facts.ToArray()
            )
        };
        
        return new MemoryTestScenario
        {
            Name = $"Multi-Session Memory ({sessionCount} sessions)",
            Description = $"Tests memory consolidation across {sessionCount} sessions with {facts.Count} total facts",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["RequiresSessionReset"] = true,
                ["SessionCount"] = sessionCount
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing memory interference across sessions.
    /// Tests whether new information in later sessions interferes with earlier memories.
    /// </summary>
    /// <param name="originalFacts">Facts established in first session</param>
    /// <param name="interferingFacts">Potentially interfering facts in second session</param>
    /// <returns>Memory interference scenario</returns>
    public static MemoryTestScenario CreateInterference(
        IReadOnlyList<MemoryFact> originalFacts,
        IReadOnlyList<MemoryFact> interferingFacts)
    {
        var steps = new List<MemoryStep>();
        
        // Session 1: Original facts
        steps.Add(MemoryStep.System("SESSION 1: Original information"));
        foreach (var fact in originalFacts)
        {
            steps.Add(MemoryStep.Fact(
                $"Please remember: {fact.Content}"
            ));
        }
        
        steps.Add(MemoryStep.System("[SESSION_RESET_POINT]"));
        
        // Session 2: Interfering facts
        steps.Add(MemoryStep.System("SESSION 2: Additional information"));
        foreach (var fact in interferingFacts)
        {
            steps.Add(MemoryStep.Fact(
                $"Please also remember: {fact.Content}"
            ));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What do you remember from our very first session?",
                originalFacts.ToArray()
            ),
            MemoryQuery.Create(
                "What do you remember from our most recent session?",
                interferingFacts.ToArray()
            ),
            MemoryQuery.Create(
                "What do you remember from all our sessions?",
                originalFacts.Concat(interferingFacts).ToArray()
            )
        };
        
        return new MemoryTestScenario
        {
            Name = "Cross-Session Memory Interference",
            Description = $"Tests whether {interferingFacts.Count} new facts interfere with recall of {originalFacts.Count} original facts",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["RequiresSessionReset"] = true
            }
        };
    }

    /// <summary>
    /// Generates specific queries for cross-session memory testing.
    /// </summary>
    private static string GenerateSpecificCrossSessionQuery(MemoryFact fact)
    {
        if (fact.Content.Contains("name", StringComparison.OrdinalIgnoreCase))
            return "From our previous session, do you remember my name?";
        if (fact.Content.Contains("birthday", StringComparison.OrdinalIgnoreCase))
            return "We talked before - when is my birthday?";
        if (fact.Content.Contains("favorite", StringComparison.OrdinalIgnoreCase))
            return "From our earlier conversation, what are my preferences?";
        if (fact.Content.Contains("allerg", StringComparison.OrdinalIgnoreCase))
            return "I mentioned dietary restrictions before - do you remember what they are?";
        
        return "What specific information did I share with you in our previous session?";
    }
}