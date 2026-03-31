// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Interface for providing standard memory test scenarios.
/// Factory for creating common memory evaluation patterns.
/// </summary>
public interface IMemoryScenarios
{
    /// <summary>
    /// Creates a basic single-session memory test with simple fact recall.
    /// </summary>
    /// <param name="facts">Facts to remember during the conversation</param>
    /// <param name="queries">Memory queries to test recall</param>
    /// <returns>Basic memory test scenario</returns>
    MemoryTestScenario CreateBasicMemoryTest(IEnumerable<MemoryFact> facts, IEnumerable<MemoryQuery> queries);
    
    /// <summary>
    /// Creates a long-term memory test spanning multiple conversation turns.
    /// </summary>
    /// <param name="facts">Facts to remember over time</param>
    /// <param name="conversationTurns">Number of conversation turns between learning and testing</param>
    /// <returns>Long-term memory test scenario</returns>
    MemoryTestScenario CreateLongTermMemoryTest(IEnumerable<MemoryFact> facts, int conversationTurns = 10);
    
    /// <summary>
    /// Creates a memory test with priority-based fact importance levels.
    /// </summary>
    /// <param name="highPriorityFacts">Critical facts that must be remembered</param>
    /// <param name="lowPriorityFacts">Optional facts that can be forgotten</param>
    /// <returns>Priority-based memory test scenario</returns>
    MemoryTestScenario CreatePriorityMemoryTest(IEnumerable<MemoryFact> highPriorityFacts, IEnumerable<MemoryFact> lowPriorityFacts);
    
    /// <summary>
    /// Creates a memory test evaluating fact updates and corrections.
    /// </summary>
    /// <param name="initialFacts">Facts learned initially</param>
    /// <param name="updatedFacts">Corrected versions of the facts</param>
    /// <returns>Memory update test scenario</returns>
    MemoryTestScenario CreateMemoryUpdateTest(IEnumerable<MemoryFact> initialFacts, IEnumerable<MemoryFact> updatedFacts);
}