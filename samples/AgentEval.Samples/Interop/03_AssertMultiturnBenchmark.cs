// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Interop.AssertAi;
using AgentEval.Results.Adapters.AssertAi;
using AgentEval.Results.Writing;

namespace AgentEval.Samples.Interop;

/// <summary>P3: offline multi-turn ASSERT observations and benchmark denominators, approximately ten seconds.</summary>
public static class AssertMultiturnBenchmark
{
    /// <summary>The sealed imported run and its separate human-readable benchmark report.</summary>
    public sealed record BenchmarkExample(string RunDirectory, string ReportFile, JsonObject Report);

    /// <summary>Finds the saved, hand-authored scenario fixture shipped beside P1's fixture.</summary>
    public static string FindExampleRun()
    {
        var root = new DirectoryInfo(AssertInteropRun.FindExampleRun()).Parent!.Parent!.Parent!.Parent!;
        return Path.Combine(root.FullName, "multiturn-example", "results", "billing-multiturn", "run-1");
    }

    /// <summary>Runs only saved-evidence processing; no target, model or judge is invoked.</summary>
    public static async Task RunAsync()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "output", "assert-multiturn-" + Guid.NewGuid().ToString("N"));
        var example = await WriteAsync(FindExampleRun(), output);
        Console.WriteLine("Hand-authored multi-turn ASSERT fixture; one observation per conversation, not per turn.");
        Console.WriteLine(example.Report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Sealed AEF: {example.RunDirectory}");
        Console.WriteLine($"Derived report (outside the seal): {example.ReportFile}");
    }

    /// <summary>Imports full saved conversations and reports case-level rates with explicit missing/error counts.</summary>
    public static async Task<BenchmarkExample> WriteAsync(string source, string output, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Path.Exists(output)) throw new ArgumentException("Expected: new output directory. Actual: destination exists. Suggestions: choose a fresh path. Because: saved evidence must not be overwritten.", nameof(output));
        var run = AssertAiRun.Read(source);
        var inputs = AssertAiTranscripts.Read(Path.Combine(source, "inference_set.jsonl"));
        var verdict = new AssertAiVerdictEval(run);
        var observations = new JsonArray();
        var byCase = inputs.ToDictionary(t => t.Key.ToString(), t => t.Input, StringComparer.Ordinal);
        var cases = run.Rows.Select(r => r.Key).Concat(run.Missing.Select(m => m.Key)).Distinct()
            .OrderBy(k => k.ToString(), StringComparer.Ordinal);
        foreach (var key in cases)
        {
            ct.ThrowIfCancellationRequested();
            var observed = byCase.TryGetValue(key.ToString(), out var input);
            input ??= new EvalInput("") { CaseId = key.ToString() };
            var turns = observed ? (IReadOnlyList<(string Role, string Text)>)input.Metadata![AssertAiTranscripts.TurnsMetadataKey] : null;
            var result = await verdict.EvaluateAsync(input, ct);
            observations.Add(new JsonObject { ["caseId"] = key.ToString(), ["messageTurns"] = turns?.Count,
                ["userTurns"] = turns?.Count(t => t.Role == "user"), ["verdict"] = result.Score.Label });
        }
        var lanes = new JsonArray();
        foreach (var headline in AssertAiHeadline.Compute(run))
            lanes.Add(new JsonObject { ["kind"] = headline.Kind, ["scoreRows"] = headline.Rows, ["scoredCases"] = headline.Scored,
                ["judgeFailures"] = headline.JudgeFailed, ["scoringSkipped"] = headline.ScoringSkipped,
                ["harmFlagged"] = headline.NotPermissible?.Flagged, ["harmDenominator"] = headline.NotPermissible?.Count,
                ["harmRate"] = headline.NotPermissible?.Rate, ["overRefusalDenominator"] = headline.Permissible?.Count,
                ["overRefusalRate"] = headline.Permissible?.Rate });
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        var imported = AssertAiImporter.Import(run, Path.Combine(output, "aef"),
            new() { TargetMode = AefTargetMode.Replayed, ContentCapture = AefContentCapture.On });
        var report = new JsonObject { ["source"] = "hand-authored offline ASSERT fixture", ["unit"] = "conversation",
            ["declaredCases"] = observations.Count, ["observedConversations"] = inputs.Count, ["missingScoreCases"] = run.Missing.Count,
            ["aefRunHash"] = imported.Verification.RunHash?.Value, ["observations"] = observations, ["lanes"] = lanes };
        var reportFile = Path.Combine(output, "benchmark.json");
        File.WriteAllText(reportFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new(imported.Directory, reportFile, report);
    }
}
