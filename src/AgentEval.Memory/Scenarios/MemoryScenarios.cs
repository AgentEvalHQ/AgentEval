// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Built-in memory testing scenarios for common evaluation patterns.
/// Provides ready-to-use scenarios that cover typical memory evaluation needs.
/// </summary>
public class MemoryScenarios : IMemoryScenarios
{
    /// <summary>
    /// Creates a basic memory retention scenario with fact establishment and recall testing.
    /// </summary>
    /// <param name="facts">Facts to establish and test</param>
    /// <returns>Memory scenario with fact setup and verification queries</returns>
    public MemoryTestScenario BasicRetention(params MemoryFact[] facts)
    {
        return BasicRetention("Basic Memory Retention", facts, GenerateQueriesForFacts(facts));
    }

    /// <summary>
    /// Creates a basic memory retention scenario with custom name and queries.
    /// </summary>
    /// <param name="name">Name for the scenario</param>
    /// <param name="facts">Facts to establish</param>
    /// <param name="queries">Custom queries to test recall</param>
    /// <returns>Configured memory scenario</returns>
    public MemoryTestScenario BasicRetention(
        string name,
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<MemoryQuery> queries)
    {
        // Create setup steps that establish each fact
        var steps = facts.Select(fact => MemoryStep.Fact(
            $"Remember: {fact.Content}"
        )).ToArray();

        return new MemoryTestScenario
        {
            Name = name,
            Description = $"Tests basic memory retention of {facts.Count} fact(s)",
            Steps = steps,
            Queries = queries
        };
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateBasicMemoryTest(IEnumerable<MemoryFact> facts, IEnumerable<MemoryQuery> queries)
    {
        return BasicRetention("Basic Memory Test", facts.ToArray(), queries.ToArray());
    }

    /// <summary>
    /// Creates a scenario testing memory retention over multiple conversation turns.
    /// </summary>
    /// <param name="facts">Facts to remember</param>
    /// <param name="conversationTurns">Number of neutral conversation turns between facts and queries</param>
    /// <returns>Memory scenario with conversation buffer</returns>
    public static MemoryTestScenario RetentionWithDelay(
        IReadOnlyList<MemoryFact> facts,
        int conversationTurns = 3)
    {
        var steps = new List<MemoryStep>();
        
        // Establish facts
        steps.AddRange(facts.Select(fact => MemoryStep.Fact(
            $"Please remember this important information: {fact.Content}"
        )));
        
        // Add neutral conversation turns
        var neutralTurns = new[]
        {
            "How are you doing today?",
            "What's the weather like?",
            "Tell me about your capabilities.",
            "What can you help me with?",
            "Do you have any questions for me?"
        };
        
        for (int i = 0; i < conversationTurns; i++)
        {
            steps.Add(MemoryStep.Conversation(neutralTurns[i % neutralTurns.Length]));
        }
        
        var queries = GenerateQueriesForFacts(facts);
        
        return new MemoryTestScenario
        {
            Name = $"Memory Retention with {conversationTurns} Turn Delay",
            Description = $"Tests memory retention after {conversationTurns} neutral conversation turns",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Creates a scenario for testing memory of categorized information.
    /// </summary>
    /// <param name="categorizedFacts">Facts grouped by category</param>
    /// <returns>Memory scenario testing category-based recall</returns>
    public static MemoryTestScenario CategorizedMemory(
        Dictionary<string, List<MemoryFact>> categorizedFacts)
    {
        var allFacts = categorizedFacts.Values.SelectMany(f => f).ToArray();
        var steps = new List<MemoryStep>();
        
        // Establish facts by category
        foreach (var (category, facts) in categorizedFacts)
        {
            steps.Add(MemoryStep.Conversation(
                $"Let me tell you some information about {category}:"));
            
            steps.AddRange(facts.Select(fact => MemoryStep.Fact(fact.Content)));
        }
        
        // Generate category-specific queries
        var queries = new List<MemoryQuery>();
        foreach (var (category, facts) in categorizedFacts)
        {
            queries.Add(MemoryQuery.Create(
                $"What do you remember about {category}?",
                facts.ToArray()
            ));
        }
        
        return new MemoryTestScenario
        {
            Name = "Categorized Memory Test",
            Description = $"Tests memory of information organized in {categorizedFacts.Count} categories",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Creates a rapid-fire memory scenario with many facts established quickly.
    /// </summary>
    /// <param name="facts">Facts to establish rapidly</param>
    /// <returns>Memory scenario with rapid fact establishment</returns>
    public static MemoryTestScenario RapidFire(params MemoryFact[] facts)
    {
        var steps = new List<MemoryStep>
        {
            MemoryStep.System("I'm going to tell you several important facts quickly. Please remember all of them.")
        };
        
        steps.AddRange(facts.Select((fact, index) => MemoryStep.Fact(
            $"{index + 1}. {fact.Content}"
        )));
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "Please list all the facts I just told you.",
                facts
            )
        };
        
        // Add individual recall queries
        queries.AddRange(GenerateQueriesForFacts(facts));
        
        return new MemoryTestScenario
        {
            Name = $"Rapid Fire Memory ({facts.Length} facts)",
            Description = $"Tests ability to quickly absorb and retain {facts.Length} facts",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Generates appropriate queries for a set of facts.
    /// </summary>
    private static IReadOnlyList<MemoryQuery> GenerateQueriesForFacts(IReadOnlyList<MemoryFact> facts)
    {
        var queries = new List<MemoryQuery>();
        
        // General recall query
        if (facts.Count > 1)
        {
            queries.Add(MemoryQuery.Create(
                "What important information do you remember from our conversation?",
                facts.ToArray()
            ));
        }
        
        // Specific queries for each fact
        foreach (var fact in facts)
        {
            var question = GenerateQuestionForFact(fact);
            queries.Add(MemoryQuery.Create(question, fact));
        }
        
        return queries;
    }

    /// <summary>
    /// Generates an appropriate question for a specific fact.
    /// </summary>
    private static string GenerateQuestionForFact(MemoryFact fact)
    {
        var content = fact.Content.ToLowerInvariant();
        
        // Pattern-based question generation
        if (content.Contains("name") && content.Contains("is"))
            return "What is my name?";
        if (content.Contains("birthday") || content.Contains("born"))
            return "When is my birthday?";
        if (content.Contains("favorite"))
            return "What are my favorite things?";
        if (content.Contains("allerg") || content.Contains("cannot eat"))
            return "Do I have any dietary restrictions or allergies?";
        if (content.Contains("job") || content.Contains("work") || content.Contains("profession"))
            return "What do you know about my work or profession?";
        if (content.Contains("live") || content.Contains("address"))
            return "Where do I live?";
        if (content.Contains("phone") || content.Contains("number"))
            return "What is my phone number?";
        if (content.Contains("email"))
            return "What is my email address?";
        
        // Default based on fact category
        return fact.Category switch
        {
            "personal" => "Tell me about my personal information.",
            "preferences" => "What are my preferences?",
            "contact" => "What are my contact details?",
            "work" => "What do you know about my work?",
            _ => "What do you remember about what I just told you?"
        };
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateLongTermMemoryTest(IEnumerable<MemoryFact> facts, int conversationTurns = 10)
    {
        var factList = facts.ToArray();
        return RetentionWithDelay(factList, conversationTurns);
    }

    /// <inheritdoc />
    public MemoryTestScenario CreatePriorityMemoryTest(IEnumerable<MemoryFact> highPriorityFacts, IEnumerable<MemoryFact> lowPriorityFacts)
    {
        var highFacts = highPriorityFacts.ToArray();
        var lowFacts = lowPriorityFacts.ToArray();
        var allFacts = highFacts.Concat(lowFacts).ToArray();
        
        var steps = new List<MemoryStep>();
        
        // Establish high priority facts first
        foreach (var fact in highFacts)
        {
            steps.Add(MemoryStep.Fact($"IMPORTANT: Please remember this critical information: {fact.Content}"));
        }
        
        // Then establish low priority facts
        foreach (var fact in lowFacts)
        {
            steps.Add(MemoryStep.Fact($"Also remember this: {fact.Content}"));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create("What are the most important things you remember?", highFacts),
            MemoryQuery.Create("What other information do you remember?", allFacts)
        };
        
        return new MemoryTestScenario
        {
            Name = "Priority-Based Memory Test",
            Description = $"Tests retention of {highFacts.Length} high-priority and {lowFacts.Length} low-priority facts",
            Steps = steps,
            Queries = queries
        };
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateMemoryUpdateTest(IEnumerable<MemoryFact> initialFacts, IEnumerable<MemoryFact> updatedFacts)
    {
        var initial = initialFacts.ToArray();
        var updated = updatedFacts.ToArray();
        
        var steps = new List<MemoryStep>();
        
        // Establish initial facts
        foreach (var fact in initial)
        {
            steps.Add(MemoryStep.Fact($"Remember: {fact.Content}"));
        }
        
        // Add some conversation
        steps.Add(MemoryStep.Conversation("Let me ask you a few questions about other things."));
        steps.Add(MemoryStep.Conversation("What are your capabilities?"));
        
        // Provide updates/corrections
        foreach (var fact in updated)
        {
            steps.Add(MemoryStep.Fact($"Actually, let me correct that information: {fact.Content}"));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create("What is the most current information you have?", updated),
            MemoryQuery.Create("Has any information been updated or corrected?", updated, initial) // Expect updated, forbid initial
        };
        
        return new MemoryTestScenario
        {
            Name = "Memory Update Test",
            Description = $"Tests ability to update {initial.Length} facts with {updated.Length} corrections",
            Steps = steps,
            Queries = queries
        };
    }
}