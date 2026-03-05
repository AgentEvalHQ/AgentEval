namespace AgentEval.Memory;

/// <summary>
/// Interface for agents that can reset their session/conversation context while preserving memory.
/// This is essential for cross-session memory testing and conversation isolation.
/// </summary>
public interface ISessionResettableAgent
{
    /// <summary>
    /// Resets the agent's conversation session/context while preserving its long-term memory.
    /// This simulates starting a new conversation with the same agent.
    /// 
    /// For example:
    /// - Clear conversation history/context
    /// - Reset session state
    /// - But preserve learned facts, user preferences, etc.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>A task representing the session reset operation</returns>
    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}