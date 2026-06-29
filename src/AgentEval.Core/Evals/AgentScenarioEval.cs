// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Evals;

/// <summary>
/// Decorates an inner judging eval (typically an <see cref="AtomicLlmEval"/> or a composite of
/// them) so that, instead of grading a single pre-supplied response, it first drives a real
/// agent-under-test with this scenario's own prompt and grades the agent's actual answer.
/// </summary>
/// <remarks>
/// <para>
/// The compliance benchmarks (GDPR / EU AI Act) historically graded one fixed <c>--response</c>
/// against every scenario's criteria, ignoring each scenario's article-specific <c>input</c>
/// prompt entirely. That makes the evidence uniform and uninformative — it never reflects how the
/// subject agent actually behaves when asked, e.g., "delete all my personal data". This wrapper
/// closes that gap: per scenario it calls <see cref="IEvaluableAgent.InvokeAsync"/> with the
/// scenario prompt, substitutes the agent's response into the <see cref="EvalInput"/>, then
/// delegates to the inner eval which runs the judge as before.
/// </para>
/// <para>
/// Identity (<see cref="Key"/> / <see cref="Name"/> / <see cref="Category"/> /
/// <see cref="Version"/>) is delegated to the inner eval so the wrapper is transparent to the
/// result tree, persistence, and reporting. An agent invocation failure yields an "error" leaf
/// rather than throwing — one unreachable agent should not abort the whole benchmark.
/// </para>
/// </remarks>
public sealed class AgentScenarioEval : IEval
{
    private readonly IEvaluableAgent _agent;
    private readonly string _scenarioInput;
    private readonly IEval _inner;

    /// <summary>Creates a scenario eval that drives <paramref name="agent"/> with
    /// <paramref name="scenarioInput"/> and grades the response via <paramref name="inner"/>.</summary>
    public AgentScenarioEval(IEvaluableAgent agent, string scenarioInput, IEval inner)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _scenarioInput = scenarioInput ?? throw new ArgumentNullException(nameof(scenarioInput));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public string Key => _inner.Key;
    /// <inheritdoc/>
    public string Name => _inner.Name;
    /// <inheritdoc/>
    public string Category => _inner.Category;
    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // An infrastructure-failure "error" leaf (severity none) — separable from a real low score and
        // never silently counted as a confirmed violation. No LLM judge runs here, so provenance is
        // "agent-error" (NOT "atomic-llm", which would misrepresent it to downstream cost/reporting),
        // and the message is length-capped so a large exception / HTTP payload can't bloat the
        // evidence and output files.
        EvalResult ErrorLeaf(string message) => new(
            Metric: new(_inner.Key, _inner.Name, _inner.Category, _inner.Version),
            Score: new(0.0, null, "error", false, null, "none", null),
            Details: new(
                Dimensions: null,
                Evidence: new[]
                {
                    new EvalEvidence(
                        Source: "agent-error",
                        Reference: _inner.Key,
                        Message: message.Length > 500 ? message[..500] + "…" : message),
                },
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("agent-error", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        string response;
        try
        {
            var agentResponse = await _agent.InvokeAsync(_scenarioInput, ct);
            response = agentResponse?.Text ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation (Ctrl+C / caller timeout) must abort the benchmark — it is NOT an
            // agent error. Let it propagate rather than recording an "error" leaf and continuing.
            throw;
        }
        catch (Exception ex)
        {
            // The agent-under-test could not be reached / errored for this scenario.
            return ErrorLeaf($"The agent under test did not return a response for this scenario: {ex.Message}");
        }

        // A null/empty/whitespace response is a non-response (e.g. a buggy adapter returning null, or
        // the agent emitting nothing) — not gradeable evidence. Treat it as an infrastructure error
        // rather than grading "" as a low score / confirmed violation.
        if (string.IsNullOrWhiteSpace(response))
            return ErrorLeaf("The agent under test returned an empty response for this scenario.");

        // Substitute the scenario prompt + the agent's real answer, then judge as usual.
        var driven = input with { Query = _scenarioInput, Response = response };
        return await _inner.EvaluateAsync(driven, ct);
    }
}
