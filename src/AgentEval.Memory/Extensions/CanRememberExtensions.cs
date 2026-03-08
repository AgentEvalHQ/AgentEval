using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Memory.Extensions;

/// <summary>
/// Extension methods for easy memory testing with one-liner APIs.
/// Provides simple ways to test agent memory without complex scenario setup.
/// </summary>
public static class CanRememberExtensions
{
    /// <summary>
    /// Tests whether an agent can remember a simple fact.
    /// This is the simplest one-liner memory test.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="fact">The fact the agent should remember</param>
    /// <param name="question">Question to test recall (if null, generates automatically)</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result</returns>
    public static async Task<MemoryEvaluationResult> CanRememberAsync(
        this IEvaluableAgent agent,
        string fact,
        string? question = null,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var memoryFact = MemoryFact.Create(fact);
        question ??= GenerateQuestion(fact);
        
        var scenarios = new MemoryScenarios();
        var scenario = scenarios.BasicRetention(
            "Basic Memory Test",
            [memoryFact],
            [MemoryQuery.Create(question, memoryFact)]
        );
        
        var runner = GetMemoryTestRunner(serviceProvider);
        return await runner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Tests whether an agent can remember multiple facts.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="facts">Facts the agent should remember</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result</returns>
    public static async Task<MemoryEvaluationResult> CanRememberAsync(
        this IEvaluableAgent agent,
        IEnumerable<string> facts,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var memoryFacts = facts.Select(MemoryFact.Create).ToArray();
        var queries = GenerateQueriesForFacts(memoryFacts);
        var scenarios = new MemoryScenarios();
        var scenario = scenarios.BasicRetention(
            "Multi-Fact Memory Test",
            memoryFacts, 
            queries);
        
        var runner = GetMemoryTestRunner(serviceProvider);
        return await runner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Tests whether an agent can remember facts with custom queries.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="factsAndQueries">Tuples of (fact, question) to test</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result</returns>
    public static async Task<MemoryEvaluationResult> CanRememberAsync(
        this IEvaluableAgent agent,
        IEnumerable<(string fact, string question)> factsAndQueries,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var items = factsAndQueries.ToArray();
        var memoryFacts = items.Select(x => MemoryFact.Create(x.fact)).ToArray();
        var queries = items.Select(x => MemoryQuery.Create(
            x.question, 
            memoryFacts.Where(f => f.Content == x.fact).ToArray()
        )).ToArray();
        
        var scenarios = new MemoryScenarios();
        var scenario = scenarios.CreateBasicMemoryTest(
            memoryFacts,
            queries
        );
        
        var runner = GetMemoryTestRunner(serviceProvider);
        return await runner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Tests whether an agent remembers facts after distracting conversation.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="facts">Facts to remember</param>
    /// <param name="distractionTurns">Number of distraction conversation turns</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result</returns>
    public static async Task<MemoryEvaluationResult> CanRememberThroughNoiseAsync(
        this IEvaluableAgent agent,
        IEnumerable<string> facts,
        int distractionTurns = 5,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        var memoryFacts = facts.Select(MemoryFact.Create).ToArray();
        var chattyScenarios = new ChattyConversationScenarios();
        var scenario = chattyScenarios.CreateBuriedFactsScenario(
            memoryFacts, 
            distractionTurns
        );
        
        var runner = GetMemoryTestRunner(serviceProvider);
        return await runner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Tests whether an agent can remember facts across session boundaries.
    /// Requires the agent to implement ISessionResettableAgent.
    /// </summary>
    /// <param name="agent">The agent to test (must implement ISessionResettableAgent)</param>
    /// <param name="facts">Facts to remember across sessions</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Memory evaluation result</returns>
    public static async Task<MemoryEvaluationResult> CanRememberAcrossSessionsAsync(
        this IEvaluableAgent agent,
        IEnumerable<string> facts,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (agent is not ISessionResettableAgent resettableAgent)
        {
            throw new InvalidOperationException(
                $"Agent {agent.GetType().Name} must implement ISessionResettableAgent for cross-session memory testing.");
        }

        var memoryFacts = facts.Select(MemoryFact.Create).ToArray();
        var crossSessionScenarios = new CrossSessionScenarios();
        var scenario = crossSessionScenarios.CreateCrossSessionMemoryTest(
            memoryFacts,
            sessionCount: 3,
            sessionGapMinutes: 60
        );
        
        var runner = GetMemoryTestRunner(serviceProvider);
        return await runner.RunAsync(agent, scenario, cancellationToken);
    }

    /// <summary>
    /// Quick memory check - tests a single fact with string matching (no LLM required).
    /// Fastest option for simple cases where exact string matching is sufficient.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="fact">The fact to check</param>
    /// <param name="question">Question to ask (if null, generates automatically)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the fact content appears in the response</returns>
    public static async Task<bool> QuickMemoryCheckAsync(
        this IEvaluableAgent agent,
        string fact,
        string? question = null,
        CancellationToken cancellationToken = default)
    {
        question ??= GenerateQuestion(fact);
        
        try
        {
            var response = await agent.InvokeAsync(question, cancellationToken);
            return response.Text.Contains(fact, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Quick memory check for multiple facts with string matching.
    /// </summary>
    /// <param name="agent">The agent to test</param>
    /// <param name="facts">Facts to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping each fact to whether it was remembered</returns>
    public static async Task<Dictionary<string, bool>> QuickMemoryCheckAsync(
        this IEvaluableAgent agent,
        IEnumerable<string> facts,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();
        
        foreach (var fact in facts)
        {
            var remembered = await agent.QuickMemoryCheckAsync(fact, null, cancellationToken);
            results[fact] = remembered;
        }
        
        return results;
    }

    /// <summary>
    /// Generates a simple question to test recall of a fact.
    /// </summary>
    private static string GenerateQuestion(string fact)
    {
        // Simple heuristic-based question generation
        if (fact.Contains("name is", StringComparison.OrdinalIgnoreCase))
            return "What is my name?";
        if (fact.Contains("birthday", StringComparison.OrdinalIgnoreCase) || fact.Contains("born", StringComparison.OrdinalIgnoreCase))
            return "When is my birthday?";
        if (fact.Contains("favorite", StringComparison.OrdinalIgnoreCase))
            return "What are my preferences?";
        if (fact.Contains("allergy", StringComparison.OrdinalIgnoreCase) || fact.Contains("allergic", StringComparison.OrdinalIgnoreCase))
            return "Do I have any allergies or dietary restrictions?";
        if (fact.Contains("job", StringComparison.OrdinalIgnoreCase) || fact.Contains("work", StringComparison.OrdinalIgnoreCase))
            return "What do you know about my work?";
        
        // Default generic question
        return "What do you remember about our previous conversations?";
    }

    /// <summary>
    /// Generates basic queries for a set of facts.
    /// </summary>
    private static IReadOnlyList<MemoryQuery> GenerateQueriesForFacts(IReadOnlyList<MemoryFact> facts)
    {
        return facts.Select(fact => MemoryQuery.Create(
            GenerateQuestion(fact.Content),
            fact
        )).ToArray();
    }

    /// <summary>
    /// Gets a memory test runner from DI or creates a minimal working default.
    /// </summary>
    private static IMemoryTestRunner GetMemoryTestRunner(IServiceProvider? serviceProvider)
    {
        if (serviceProvider != null)
        {
            var runner = serviceProvider.GetService<IMemoryTestRunner>();
            if (runner != null) return runner;
        }
        
        // Create minimal working default for scenarios without DI
        var logger = serviceProvider?.GetService<ILogger<MemoryTestRunner>>() ?? NullLogger<MemoryTestRunner>.Instance;
        var chatClient = serviceProvider?.GetService<IChatClient>() ?? 
            throw new InvalidOperationException(
                "No IChatClient available. Please provide IServiceProvider with registered IChatClient or " +
                "use services.AddAgentEvalMemory() for proper dependency injection setup.");
                
        var judgeLogger = serviceProvider?.GetService<ILogger<MemoryJudge>>() ?? NullLogger<MemoryJudge>.Instance;
        var judge = new MemoryJudge(chatClient, judgeLogger);
        
        return new MemoryTestRunner(judge, logger);
    }
}