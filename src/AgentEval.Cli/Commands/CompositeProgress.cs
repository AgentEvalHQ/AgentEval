// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;

namespace AgentEval.Cli.Commands;

/// <summary>Retains one invocation's completed tree nodes; callbacks may arrive from parallel children.</summary>
internal sealed class CompositeProgress(CompositeEval benchmark)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _started = [];
    private readonly Dictionary<string, EvalResult> _completed = [];

    public void Started(IReadOnlyList<int> path)
    {
        lock (_sync) _started.Add(string.Join('/', path));
    }

    public void Completed(IReadOnlyList<int> path, EvalResult result)
    {
        lock (_sync) _completed.Add(string.Join('/', path), result);
    }

    public EvalResult Snapshot()
    {
        lock (_sync) return Visit(benchmark, "");
    }

    private EvalResult Visit(IEval eval, string path)
    {
        if (_completed.TryGetValue(path, out var result)) return result;
        var composite = eval as CompositeEval;
        var children = composite?.Components.Select((c, i) =>
        {
            var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Visit(c.Eval, path.Length == 0 ? index : path + "/" + index);
        }).ToList();
        return new EvalResult(new(eval.Key, eval.Name, eval.Category, eval.Version),
            new EvalScore(0, null, _started.Contains(path) ? "error" : "skipped", false, composite?.Threshold, "none", null)
            {
                Measurement = MeasurementState.NotMeasured,
            },
            new(null, null, null, children, composite?.Aggregation.Name),
            new(composite is null ? "atomic-code" : "composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
    }
}
