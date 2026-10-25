// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    // These two producers wrote EvalResult, in System.Text.Json's default wire spelling, without scenarios.
    // Other report-native.json families have different DTOs; retaining their bytes does not infer a case model.
    private static IReadOnlyList<ScenarioResult>? TraceReportScenarios(string snapshot, RunManifest? manifest)
    {
        if (manifest?.Run?.Harness is not ("BenchTraceFidelityCommand" or "BenchWorkflowTraceFidelityCommand")) return null;
        var bytes = NativeReportBytes(snapshot);
        if (bytes is null) return null; // An interrupted run may not have published its report.
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

    // These producers count question rows, not ScenarioResult files. Recognize the recorded DTO's unit
    // without turning it into evaluator evidence that the original producer never recorded.
    private static int? ReportOnlyQuestionCount(string snapshot, RunManifest? manifest, int scenarioCount)
    {
        if (scenarioCount != 0 || manifest?.Run?.Harness is not ("BenchLongMemEvalCommand" or "BenchTypedMemEvalCommand")) return null;
        var bytes = NativeReportBytes(snapshot);
        if (bytes is null) return null;
        JsonObject report;
        try { report = AefJsonReader.ParseDocument(bytes, 64 << 20); }
        catch (AefReadException error) { throw InvalidSource("The external memory report is not bounded unambiguous UTF-8 JSON.", error); }
        var id = AefNode.String(report["BenchmarkId"]);
        var familyMatches = manifest.Run.Harness == "BenchLongMemEvalCommand" ? id == "longmemeval"
            : id is not null && id.StartsWith("typedmemeval-", StringComparison.Ordinal) && id.Length > "typedmemeval-".Length;
        if (!familyMatches || report["QuestionResults"] is not JsonArray questions
            || report["SelectedQuestions"] is not JsonValue count || !count.TryGetValue<int>(out var selected)
            || selected != questions.Count || questions.Any(q => q is not JsonObject row || string.IsNullOrWhiteSpace(AefNode.String(row["QuestionId"]))))
            throw InvalidSource("The external memory report does not identify its producer and recorded question count consistently.");
        return selected;
    }

    private static byte[]? NativeReportBytes(string snapshot)
    {
        var path = Path.Combine(snapshot, "report-native.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64L << 20) throw InvalidSource("The native report exceeds 64 MiB.");
        return File.ReadAllBytes(path);
    }
}
