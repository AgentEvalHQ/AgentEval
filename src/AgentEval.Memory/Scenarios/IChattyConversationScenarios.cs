using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Interface for creating memory scenarios with conversational noise and distractions.
/// Tests memory resilience in realistic chatty environments.
/// </summary>
public interface IChattyConversationScenarios
{
    /// <summary>
    /// Creates a memory test where important facts are buried in lengthy conversations.
    /// </summary>
    /// <param name="importantFacts">Key facts to remember despite noise</param>
    /// <param name="noiseRatio">Ratio of noise messages to important facts (e.g., 5:1)</param>
    /// <returns>Chatty conversation memory test scenario</returns>
    MemoryTestScenario CreateBuriedFactsScenario(IEnumerable<MemoryFact> importantFacts, double noiseRatio = 3.0);
    
    /// <summary>
    /// Creates a memory test with frequent topic changes and distractions.
    /// </summary>
    /// <param name="facts">Facts to remember across topic changes</param>
    /// <param name="topicChanges">Number of topic switches during conversation</param>
    /// <returns>Topic-switching memory test scenario</returns>
    MemoryTestScenario CreateTopicSwitchingScenario(IEnumerable<MemoryFact> facts, int topicChanges = 5);
    
    /// <summary>
    /// Creates a memory test with emotional distractors and off-topic discussions.
    /// </summary>
    /// <param name="facts">Important facts to remember</param>
    /// <param name="emotionalIntensity">Level of emotional distraction (1-10)</param>
    /// <returns>Emotionally distracting memory test scenario</returns>
    MemoryTestScenario CreateEmotionalDistractorScenario(IEnumerable<MemoryFact> facts, int emotionalIntensity = 5);
    
    /// <summary>
    /// Creates a memory test with false information that should be ignored or corrected.
    /// </summary>
    /// <param name="trueFacts">Correct facts to remember</param>
    /// <param name="falseFacts">Incorrect facts that should be rejected</param>
    /// <returns>False information filtering memory test scenario</returns>
    MemoryTestScenario CreateFalseInformationScenario(IEnumerable<MemoryFact> trueFacts, IEnumerable<MemoryFact> falseFacts);
}