// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Configurable retry policy for LLM and embedding operations.
/// Inspired by kbeaugrand/KernelMemory.Evaluation retry patterns.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Maximum number of retry attempts.</summary>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>Initial delay between retries in milliseconds.</summary>
    public int InitialDelayMs { get; init; } = 100;
    
    /// <summary>Multiplier for exponential backoff.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;
    
    /// <summary>Maximum delay between retries in milliseconds.</summary>
    public int MaxDelayMs { get; init; } = 5000;
    
    /// <summary>Default retry policy (3 retries with exponential backoff).</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>No retries policy.</summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };

    /// <summary>
    /// Optional predicate selecting which exceptions are worth retrying. <see langword="null"/> (the
    /// default) preserves today's behavior for every existing caller — every exception except
    /// <see cref="OperationCanceledException"/> is retried. When set and it returns <see langword="false"/>
    /// for a caught exception, that exception is rethrown IMMEDIATELY (not wrapped in
    /// <see cref="RetryExhaustedException"/>, and without waiting) instead of wasting the retry budget on a
    /// failure known to be permanent — e.g. an auth failure should fail fast, not retry into a rate-limit's
    /// backoff schedule. See <c>AgentEval.MAF.CopilotStudio.CopilotStudioRetryPolicy</c> for the first
    /// consumer (retry only a 429-shaped <see cref="HttpRequestException"/>).
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    /// <summary>
    /// Execute an async function with retry logic.
    /// </summary>
    /// <typeparam name="T">Return type.</typeparam>
    /// <param name="action">The async action to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the action.</returns>
    /// <exception cref="RetryExhaustedException">Thrown when all retries are exhausted.</exception>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var exceptions = new List<Exception>();
        var currentDelay = InitialDelayMs;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Don't retry on explicit cancellation
            }
            catch (Exception ex)
            {
                var policyFailure = ex is RetryPolicyExtensions.FailedCallException { Result.CallFailure: { } original }
                    ? original : ex;
                if (ShouldRetry is not null && !ShouldRetry(policyFailure))
                {
                    throw; // a permanent failure (e.g. auth) — don't waste the retry budget
                }

                exceptions.Add(ex);

                if (attempt == MaxRetries)
                {
                    break; // No more retries
                }

                // Wait before retry with exponential backoff
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                currentDelay = Math.Min((int)(currentDelay * BackoffMultiplier), MaxDelayMs);
            }
        }

        throw new RetryExhaustedException(
            $"Operation failed after {MaxRetries + 1} attempt(s).",
            exceptions);
    }
    
    /// <summary>
    /// Execute an async function with retry logic (simple overload).
    /// Pattern inspired by kbeaugrand: Try(3, async (remainingTry) => {...})
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(_ => action(), cancellationToken);
}

/// <summary>
/// Exception thrown when all retry attempts have been exhausted.
/// </summary>
public class RetryExhaustedException : Exception
{
    /// <summary>All exceptions that occurred during retry attempts.</summary>
    public IReadOnlyList<Exception> Attempts { get; }
    
    /// <summary>Creates a new retry exhausted exception.</summary>
    public RetryExhaustedException(string message, IEnumerable<Exception> attempts) 
        : base(message, attempts.LastOrDefault())
    {
        Attempts = attempts.ToList().AsReadOnly();
    }
}

/// <summary>
/// Extension methods for using RetryPolicy with metrics.
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Execute a metric evaluation with retry.
    /// </summary>
    /// <remarks>
    /// A metric that throws is retried, and so is one that reports its own failed judge call as not measured
    /// (<see cref="NotMeasuredCause.JudgeCallFailed"/>): the built-in LLM metrics catch the call and no longer throw.
    /// When the attempts run out the result is not measured, with the last error; it was a Fail at score 0, a score
    /// nothing measured. The caller's cancellation propagates.
    /// </remarks>
    public static async Task<MetricResult> EvaluateWithRetryAsync(
        this IMetric metric,
        EvaluationContext context,
        RetryPolicy? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        var policy = retryPolicy ?? RetryPolicy.Default;
        var attempts = policy.MaxRetries + 1;

        try
        {
            return await policy.ExecuteAsync(
                async ct =>
                {
                    var result = await metric.EvaluateAsync(context, ct).ConfigureAwait(false);
                    return result.NotMeasuredCause == NotMeasuredCause.JudgeCallFailed
                        ? throw new FailedCallException(result)
                        : result;
                },
                cancellationToken);
        }
        catch (RetryExhaustedException ex) when (ex.InnerException is FailedCallException { Result: var last })
        {
            var details = new Dictionary<string, object>(last.Details ?? new Dictionary<string, object>())
            {
                ["attemptCount"] = attempts,
                ["lastError"] = last.Explanation ?? "Unknown error"
            };
            return new MetricResult
            {
                MetricName = last.MetricName, Score = 0, Passed = false, Measured = false,
                NotMeasuredCause = NotMeasuredCause.JudgeCallFailed,
                Explanation = $"{last.Explanation} The evaluation failed after {attempts} attempts.",
                Details = details, ModelGraded = last.ModelGraded, RubricDigest = last.RubricDigest,
                CallFailure = last.CallFailure,
            };
        }
        catch (RetryExhaustedException ex)
        {
            var error = ex.InnerException;
            return MetricResult.NotMeasured(
                metric.Name,
                NotMeasuredCause.JudgeCallFailed,
                $"{metric.Name} not measured: the evaluation failed after {attempts} attempts " +
                $"({error?.GetType().Name}: {error?.Message}).",
                new Dictionary<string, object>
                {
                    ["attemptCount"] = attempts,
                    ["lastError"] = error?.Message ?? "Unknown error",
                    ["errorType"] = error?.GetType().FullName ?? "Unknown",
                    ["errorMessage"] = error?.Message ?? "Unknown error"
                });
        }
        catch (FailedCallException ex)
        {
            // The policy's ShouldRetry declined it: the metric's own not-measured result stands.
            return ex.Result;
        }
    }

    // Carries a metric's "the judge call failed" result through RetryPolicy, which retries on a throw.
    internal sealed class FailedCallException(MetricResult result) : Exception(result.Explanation, result.CallFailure)
    {
        public MetricResult Result { get; } = result;
    }
}
