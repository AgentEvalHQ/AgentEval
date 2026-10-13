// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Collections.Concurrent;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Associates observed calls with individual evaluation results, including repeated trials.</summary>
internal sealed class CliUsageEvaluationHarness(IEvaluationHarness inner) : IEvaluationHarness
{
    private readonly ConcurrentDictionary<TestResult, IReadOnlyList<AefUsage>> _usage = new(ReferenceEqualityComparer.Instance);

    public async Task<TestResult> RunEvaluationAsync(IEvaluableAgent agent, TestCase testCase,
        EvaluationOptions? options = null, CancellationToken cancellationToken = default)
    {
        // An internal attribution key, never an exported identity or content field.
        var key = Guid.NewGuid().ToString("N");
        using var scope = CliUsageMeter.Attribute(caseId: key);
        var result = await inner.RunEvaluationAsync(agent, testCase, options, cancellationToken).ConfigureAwait(false);
        if (CliUsageMeter.ForCase(key) is { } usage) _usage[result] = usage;
        return result;
    }

    internal IReadOnlyList<AefUsage>? TakeUsage(TestResult result) =>
        _usage.TryRemove(result, out var usage) ? usage : null;
}
