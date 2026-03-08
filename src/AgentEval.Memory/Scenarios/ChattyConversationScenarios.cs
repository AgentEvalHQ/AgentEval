using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Scenarios for testing memory retention in noisy, chatty conversations.
/// These scenarios bury important facts among distracting conversation turns.
/// </summary>
public class ChattyConversationScenarios : IChattyConversationScenarios
{
    /// <inheritdoc />
    public MemoryTestScenario CreateBuriedFactsScenario(IEnumerable<MemoryFact> importantFacts, double noiseRatio = 3.0)
    {
        return BuriedFacts(importantFacts.ToArray(), (int)noiseRatio);
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateTopicSwitchingScenario(IEnumerable<MemoryFact> facts, int topicChanges = 5)
    {
        return RapidTopicChanges(facts.ToArray(), topicChanges);
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateEmotionalDistractorScenario(IEnumerable<MemoryFact> facts, int emotionalIntensity = 5)
    {
        return EmotionalDistractors(facts.ToArray());
    }

    /// <inheritdoc />
    public MemoryTestScenario CreateFalseInformationScenario(IEnumerable<MemoryFact> trueFacts, IEnumerable<MemoryFact> falseFacts)
    {
        var trueFactsList = trueFacts.ToArray();
        var falseFactsList = falseFacts.ToArray();
        var steps = new List<MemoryStep>();

        // Interleave true and false facts
        var maxCount = Math.Max(trueFactsList.Length, falseFactsList.Length);
        for (int i = 0; i < maxCount; i++)
        {
            if (i < trueFactsList.Length)
            {
                steps.Add(MemoryStep.Fact($"Please remember: {trueFactsList[i].Content}"));
            }
            if (i < falseFactsList.Length)
            {
                steps.Add(MemoryStep.Noise($"Actually, I heard that {falseFactsList[i].Content}"));
            }
        }

        // Add a correction step
        steps.Add(MemoryStep.System("Note: some of the previous statements were incorrect. Only trust the facts I explicitly asked you to remember."));

        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What are the facts I explicitly asked you to remember? Please only include information I specifically asked you to keep in mind.",
                trueFactsList,
                falseFactsList
            )
        };

        return new MemoryTestScenario
        {
            Name = "False Information Filtering",
            Description = $"Tests ability to distinguish {trueFactsList.Length} true facts from {falseFactsList.Length} false facts",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Creates a scenario where important facts are buried among distracting conversation.
    /// </summary>
    public MemoryTestScenario BuriedFacts(
        IReadOnlyList<MemoryFact> facts,
        int noiseRatio = 5)
    {
        var steps = new List<MemoryStep>();
        var noiseTurns = GenerateNoiseTurns();
        var noiseIndex = 0;
        
        // Start with some noise
        for (int i = 0; i < noiseRatio / 2; i++)
        {
            steps.Add(MemoryStep.Noise(noiseTurns[noiseIndex++ % noiseTurns.Length]));
        }
        
        // Interleave facts with noise
        foreach (var fact in facts)
        {
            // Add the important fact
            steps.Add(MemoryStep.Fact(
                $"By the way, {fact.Content.ToLowerInvariant()}"
            ));
            
            // Add noise turns after the fact
            for (int i = 0; i < noiseRatio; i++)
            {
                steps.Add(MemoryStep.Noise(noiseTurns[noiseIndex++ % noiseTurns.Length]));
            }
        }
        
        // End with more noise
        for (int i = 0; i < noiseRatio / 2; i++)
        {
            steps.Add(MemoryStep.Noise(noiseTurns[noiseIndex++ % noiseTurns.Length]));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "Despite all our chatting, what important information should you remember about me?",
                facts.ToArray()
            )
        };
        
        // Add specific queries for each fact
        foreach (var fact in facts)
        {
            queries.Add(MemoryQuery.Create(
                GenerateSpecificQuery(fact),
                fact
            ));
        }
        
        return new MemoryTestScenario
        {
            Name = $"Buried Facts in Chatty Conversation (1:{noiseRatio} signal-to-noise)",
            Description = $"Tests memory retention when {facts.Count} important facts are buried among {steps.Count - facts.Count} distracting conversation turns",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Creates a scenario with rapid topic changes to test focus and retention.
    /// </summary>
    /// <param name="facts">Facts to remember</param>
    /// <param name="topicCount">Number of different topics to discuss</param>
    /// <returns>Topic-switching memory scenario</returns>
    public MemoryTestScenario RapidTopicChanges(
        IReadOnlyList<MemoryFact> facts,
        int topicCount = 10)
    {
        var steps = new List<MemoryStep>();
        var topics = GenerateTopics();
        var topicIndex = 0;
        
        // Scatter facts among rapid topic changes
        for (int i = 0; i < Math.Max(facts.Count * 3, topicCount); i++)
        {
            if (i < facts.Count && (i % 3 == 1)) // Insert facts occasionally
            {
                steps.Add(MemoryStep.Fact(
                    $"Oh, I almost forgot to mention: {facts[i].Content}"
                ));
            }
            else
            {
                var topic = topics[topicIndex++ % topics.Length];
                steps.Add(MemoryStep.Noise(
                    $"Let's talk about {topic}. What do you think about that?"
                ));
            }
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "We covered many topics, but what specific facts did I ask you to remember?",
                facts.ToArray()
            )
        };
        
        return new MemoryTestScenario
        {
            Name = $"Rapid Topic Changes with {facts.Count} Embedded Facts",
            Description = $"Tests focus and retention during rapid topic switching with embedded facts",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Creates a scenario with emotional/engaging distractors.
    /// </summary>
    /// <param name="facts">Facts to remember</param>
    /// <returns>Emotionally distracting memory scenario</returns>
    public MemoryTestScenario EmotionalDistractors(IReadOnlyList<MemoryFact> facts)
    {
        var steps = new List<MemoryStep>();
        var emotionalDistracters = GenerateEmotionalDistractors();
        var distractorIndex = 0;
        
        // Pattern: fact -> emotional distractors -> fact -> ...
        foreach (var fact in facts)
        {
            steps.Add(MemoryStep.Fact(
                $"Please remember: {fact.Content}"
            ));
            
            // Add 2-3 emotional distractors
            for (int i = 0; i < 3; i++)
            {
                steps.Add(MemoryStep.Noise(
                    emotionalDistracters[distractorIndex++ % emotionalDistracters.Length]
                ));
            }
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "Setting aside our emotional discussion, what factual information did I ask you to remember?",
                facts.ToArray()
            )
        };
        
        return new MemoryTestScenario
        {
            Name = "Memory Through Emotional Distractors",
            Description = "Tests memory retention when facts are mixed with emotionally engaging content",
            Steps = steps,
            Queries = queries
        };
    }

    /// <summary>
    /// Generates neutral noise conversation turns.
    /// </summary>
    private static string[] GenerateNoiseTurns() => new[]
    {
        "How's the weather today?",
        "What do you think about artificial intelligence?",
        "Do you have any hobbies?",
        "What's your favorite color?",
        "Tell me a joke.",
        "What's 2+2?",
        "How are you feeling?",
        "What can you help me with?",
        "Do you like music?",
        "What's the capital of France?",
        "Can you explain quantum physics?",
        "What's your favorite movie?",
        "How does machine learning work?",
        "What's for lunch?",
        "Tell me about yourself.",
        "What's the meaning of life?",
        "Can you write a poem?",
        "What's the time?",
        "How do computers work?",
        "What's your favorite book?"
    };

    /// <summary>
    /// Generates topic names for rapid topic switching.
    /// </summary>
    private static string[] GenerateTopics() => new[]
    {
        "space exploration",
        "cooking recipes",
        "travel destinations",
        "historical events",
        "sports teams",
        "movie genres",
        "scientific discoveries",
        "art movements",
        "programming languages",
        "musical instruments",
        "wildlife conservation",
        "architecture styles",
        "cultural traditions",
        "technological innovations",
        "fashion trends"
    };

    /// <summary>
    /// Generates emotionally engaging distractors.
    /// </summary>
    private static string[] GenerateEmotionalDistractors() => new[]
    {
        "I'm so excited about this new project I'm working on!",
        "I'm really worried about climate change, aren't you?",
        "That movie made me cry so much last night.",
        "I'm absolutely furious about the traffic this morning.",
        "I feel so grateful for all the good things in my life.",
        "This news story is just heartbreaking.",
        "I'm incredibly proud of my friend's achievement.",
        "I'm so nervous about my upcoming presentation.",
        "That joke was hilarious, I can't stop laughing!",
        "I'm feeling quite overwhelmed with everything lately.",
        "This music gives me chills every time.",
        "I'm so disappointed with how things turned out.",
        "That story was absolutely inspiring.",
        "I'm really confused about this whole situation.",
        "This makes me feel so nostalgic about childhood."
    };

    /// <summary>
    /// Generates a specific query for a buried fact.
    /// </summary>
    private static string GenerateSpecificQuery(MemoryFact fact)
    {
        if (fact.Content.Contains("name", StringComparison.OrdinalIgnoreCase))
            return "Despite all our chatting, do you remember my name?";
        if (fact.Content.Contains("birthday", StringComparison.OrdinalIgnoreCase))
            return "Among everything we discussed, do you recall when my birthday is?";
        if (fact.Content.Contains("favorite", StringComparison.OrdinalIgnoreCase))
            return "We talked about many things, but do you remember my preferences?";
        
        return "In all our conversation, what specific fact did I mention about myself?";
    }
}