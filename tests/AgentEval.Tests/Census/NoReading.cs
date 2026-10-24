// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Census;

/// <summary>
/// One assertion per kind of output that a nothing-measured input must give: no number and no verdict. Each names the
/// census row it checks, so a failure says which producer broke the rule.
/// </summary>
internal static class NoReading
{
    /// <summary>
    /// An eval score that is not a reading: it counts toward no aggregate, it did not pass, a report shows ERROR or
    /// SKIPPED (WARN only for a withheld pass, whose state says not measured), and the AEF writer records an absence.
    /// </summary>
    internal static void Score(EvalScore score, string row)
    {
        Assert.False(score.CountsTowardAggregate(), $"{row}: the score counts toward an aggregate ({score.Label}, {score.Measurement})");
        Assert.False(score.Passed, $"{row}: the score passed");
        Assert.Null(score.MeasuredValue());

        var status = score.ReportStatus();
        Assert.True(status is "ERROR" or "SKIPPED" || (status == "WARN" && score.Measurement == MeasurementState.NotMeasured),
            $"{row}: a report shows {status} for it");

        var state = AefCompositeRunWriter.StateOf(score);
        Assert.True(state is AefState.Error or AefState.Skipped or AefState.NotMeasured or AefState.NotApplicable,
            $"{row}: the AEF writer records it as {state}");
    }

    /// <summary>A metric result that is not a reading: not measured, not passed, and it says why.</summary>
    internal static void Metric(MetricResult result, string row)
    {
        Assert.False(result.Measured, $"{row}: the metric is measured (score {result.Score})");
        Assert.False(result.Passed, $"{row}: the metric passed");
        Assert.True(result.NotMeasuredCause is not null, $"{row}: the metric gives no typed cause");
    }

    /// <summary>A number nobody measured is absent.</summary>
    internal static void Number(double? value, string row) =>
        Assert.True(value is null, $"{row}: a number was reported ({value}) where nothing was measured");

    /// <summary>A plain <see cref="double"/> that cannot be null says "nothing" with NaN.</summary>
    internal static void NaN(double value, string row) =>
        Assert.True(double.IsNaN(value), $"{row}: {value} was reported where nothing was measured; NaN says so");

    /// <summary>A key whose value would be a placeholder is left out.</summary>
    internal static void NoKey<TValue>(IReadOnlyDictionary<string, TValue> dictionary, string key, string row)
    {
        if (dictionary.TryGetValue(key, out var value))
            Assert.Fail($"{row}: '{key}' was written ({value}) where nothing was measured");
    }

    /// <summary>A producer with nothing to report refuses, rather than report a placeholder.</summary>
    internal static async Task<TException> Refused<TException>(Func<Task> call, string row)
        where TException : Exception
    {
        try
        {
            await call();
        }
        catch (TException ex)
        {
            return ex;
        }

        Assert.Fail($"{row}: the call returned where nothing was measured; it should have thrown {typeof(TException).Name}");
        throw new InvalidOperationException("unreachable");
    }
}
