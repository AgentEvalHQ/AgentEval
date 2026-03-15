using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Extensions;

/// <summary>
/// Extension methods to bridge MemoryEvaluationResult into the core EvaluationContext pipeline,
/// enabling memory metrics (MemoryRetentionMetric, MemoryTemporalMetric, etc.) to function.
/// </summary>
public static class MemoryEvaluationContextExtensions
{
    /// <summary>
    /// The property key used to store/retrieve MemoryEvaluationResult in an EvaluationContext.
    /// </summary>
    public const string MemoryResultKey = "MemoryEvaluationResult";

    /// <summary>
    /// Converts a MemoryEvaluationResult into an EvaluationContext populated with the
    /// properties that memory metrics expect.
    /// </summary>
    /// <example>
    /// <code>
    /// var result = await runner.RunAsync(agent, scenario);
    /// var context = result.ToEvaluationContext();
    /// var metricResult = await retentionMetric.EvaluateAsync(context);
    /// </code>
    /// </example>
    public static EvaluationContext ToEvaluationContext(this MemoryEvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lastQuery = result.QueryResults.Count > 0 ? result.QueryResults[^1] : null;

        var context = new EvaluationContext
        {
            Input = lastQuery?.Query.Question ?? result.ScenarioName,
            Output = lastQuery?.Response ?? string.Empty
        };

        context.SetProperty(MemoryResultKey, result);

        return context;
    }
}
