// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Json;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    // These two producers wrote EvalResult, in System.Text.Json's default wire spelling, without scenarios.
    // Other report-native.json families have different DTOs; retaining their bytes does not infer a case model.
    private static IReadOnlyList<ScenarioResult>? TraceReportScenarios(string snapshot, RunManifest? manifest)
    {
        if (manifest?.Run?.Harness is not ("BenchTraceFidelityCommand" or "BenchWorkflowTraceFidelityCommand")) return null;
        var path = Path.Combine(snapshot, "report-native.json");
        if (!File.Exists(path)) return null; // An interrupted run may not have published its report.
        if (new FileInfo(path).Length > 64L << 20) throw InvalidSource("The native trace report exceeds 64 MiB.");
        var bytes = File.ReadAllBytes(path);
        try
        {
            AefJsonReader.Check(bytes, 64 << 20);
            var tree = StoreV1EvalTree.Parse(new UTF8Encoding(false, true).GetString(bytes), nativePropertyNames: true);
            var cases = tree.Details.SubResults is { Count: > 0 } children ? children : [tree];
            return cases.Select(c => EvalResultPersistence.ToScenarioResult(c, c.Metric.Key, c.Metric.Name, [])).ToArray();
        }
        catch (Exception error) when (error is AefEncodingException or DecoderFallbackException)
        { throw InvalidSource("The native trace report is not readable unambiguous UTF-8 JSON.", error); }
    }
}
