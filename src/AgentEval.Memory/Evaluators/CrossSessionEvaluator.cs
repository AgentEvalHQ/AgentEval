using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Evaluates memory persistence across session boundaries.
/// Plants facts in session 1, resets the session, then tests recall in session 2.
/// Requires the agent to implement <see cref="ISessionResettableAgent"/>.
/// </summary>
public class CrossSessionEvaluator : ICrossSessionEvaluator
{
    private readonly IMemoryJudge _judge;
    private readonly ILogger<CrossSessionEvaluator> _logger;

    public CrossSessionEvaluator(IMemoryJudge judge, ILogger<CrossSessionEvaluator> logger)
    {
        _judge = judge;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CrossSessionResult> EvaluateAsync(
        IEvaluableAgent agent,
        IReadOnlyList<MemoryFact> facts,
        double successThreshold = 0.8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(facts);

        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting cross-session evaluation with {FactCount} facts", facts.Count);

        // Check if the agent supports session reset
        if (agent is not ISessionResettableAgent resettable)
        {
            stopwatch.Stop();
            _logger.LogWarning("Agent does not implement ISessionResettableAgent. Cross-session evaluation requires session reset capability.");

            return new CrossSessionResult
            {
                ScenarioName = "CrossSession",
                Passed = false,
                OverallScore = 0,
                FactResults = [],
                SessionResetSupported = false,
                ErrorMessage = "Agent does not implement ISessionResettableAgent. Cross-session testing requires session reset capability.",
                Duration = stopwatch.Elapsed
            };
        }

        try
        {
            // SESSION 1: Plant facts
            _logger.LogDebug("Session 1: Planting {FactCount} facts", facts.Count);
            foreach (var fact in facts)
            {
                await agent.InvokeAsync($"Please remember this: {fact.Content}", cancellationToken);
            }

            // Confirm the agent acknowledged
            await agent.InvokeAsync("Can you confirm you've noted all this information?", cancellationToken);

            // SESSION RESET
            _logger.LogDebug("Resetting session...");
            await resettable.ResetSessionAsync(cancellationToken);

            // SESSION 2: Test recall
            _logger.LogDebug("Session 2: Testing recall of {FactCount} facts", facts.Count);
            var factResults = new List<CrossSessionFactResult>();

            foreach (var fact in facts)
            {
                var question = $"What do you know about: {string.Join(' ', fact.Content.Split(' ').Take(4))}?";
                var response = await agent.InvokeAsync(question, cancellationToken);

                var query = MemoryQuery.Create(question, fact);
                var judgment = await _judge.JudgeAsync(response.Text, query, cancellationToken);

                factResults.Add(new CrossSessionFactResult
                {
                    Fact = fact.Content,
                    Query = question,
                    Response = response.Text,
                    Recalled = judgment.Score >= 80,
                    Score = judgment.Score
                });
            }

            stopwatch.Stop();

            var passRate = factResults.Count > 0
                ? factResults.Count(r => r.Recalled) / (double)factResults.Count
                : 0;

            var result = new CrossSessionResult
            {
                ScenarioName = "CrossSession",
                Passed = passRate >= successThreshold,
                OverallScore = passRate * 100,
                FactResults = factResults,
                SessionResetSupported = true,
                SessionResetCount = 1,
                Duration = stopwatch.Elapsed
            };

            _logger.LogInformation("Cross-session evaluation complete: {Retained}/{Total} facts survived ({Score:F1}% retention)",
                result.RetainedCount, facts.Count, result.OverallScore);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cross-session evaluation");
            stopwatch.Stop();

            return new CrossSessionResult
            {
                ScenarioName = "CrossSession",
                Passed = false,
                OverallScore = 0,
                FactResults = [],
                SessionResetSupported = true,
                ErrorMessage = $"Error during cross-session evaluation: {ex.Message}",
                Duration = stopwatch.Elapsed
            };
        }
    }
}
