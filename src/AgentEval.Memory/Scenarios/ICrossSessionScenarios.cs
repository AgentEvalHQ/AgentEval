// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.Scenarios;

/// <summary>
/// Interface for creating memory scenarios that span multiple conversation sessions.
/// Tests persistent memory across session boundaries.
/// </summary>
public interface ICrossSessionScenarios
{
    /// <summary>
    /// Creates a memory test that spans multiple conversation sessions with session resets.
    /// </summary>
    /// <param name="facts">Facts to remember across sessions</param>
    /// <param name="sessionCount">Number of separate conversation sessions</param>
    /// <param name="sessionGapMinutes">Simulated time gap between sessions in minutes</param>
    /// <returns>Cross-session memory test scenario</returns>
    MemoryTestScenario CreateCrossSessionMemoryTest(
        IEnumerable<MemoryFact> facts, 
        int sessionCount = 3, 
        int sessionGapMinutes = 60);
    
    /// <summary>
    /// Creates a memory test evaluating fact persistence after agent restarts.
    /// </summary>
    /// <param name="facts">Facts that should persist through restarts</param>
    /// <param name="restartCount">Number of agent restart cycles</param>
    /// <returns>Restart persistence memory test scenario</returns>
    MemoryTestScenario CreateRestartPersistenceTest(IEnumerable<MemoryFact> facts, int restartCount = 2);
    
    /// <summary>
    /// Creates a memory test with gradual fact accumulation across multiple sessions.
    /// </summary>
    /// <param name="factsPerSession">Facts to learn in each session</param>
    /// <returns>Incremental learning memory test scenario</returns>
    MemoryTestScenario CreateIncrementalLearningTest(IEnumerable<IEnumerable<MemoryFact>> factsPerSession);
    
    /// <summary>
    /// Creates a memory test evaluating context switching between different conversation contexts.
    /// </summary>
    /// <param name="contextFacts">Facts specific to different contexts</param>
    /// <param name="contextNames">Names/identifiers for each context</param>
    /// <returns>Context switching memory test scenario</returns>
    MemoryTestScenario CreateContextSwitchingTest(
        IEnumerable<IEnumerable<MemoryFact>> contextFacts, 
        IEnumerable<string> contextNames);
}