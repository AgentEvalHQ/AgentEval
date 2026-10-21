// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// The golden tests' fixed-score judge, recording every input it was sent. A golden tool test checks with it that the
/// judge was shown the run's tool calls: the tests used to grade prose such as "I called search_flights." with no tool
/// calls at all, so they exercised the judge's reading of the reply, not the tool data (T68).
/// </summary>
internal sealed class RecordingScoreEvaluator(int score) : IEvaluator
{
    private readonly FixedScoreEvaluator _inner = new(score);
    private readonly List<string> _inputs = new();

    /// <summary>Every judge input sent so far, in order.</summary>
    public IReadOnlyList<string> Inputs
    {
        get { lock (_inputs) return _inputs.ToList(); }
    }

    public Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken ct = default)
    {
        lock (_inputs) _inputs.Add(input);
        return _inner.EvaluateAsync(input, output, criteria, ct);
    }
}
