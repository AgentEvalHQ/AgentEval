// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Interop.AssertAi;
using AgentEval.Results.Adapters.AssertAi;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Samples.Interop;

/// <summary>P2: offline ASSERT import and composite evidence, approximately ten seconds; no model or HTTP calls.</summary>
public static class AssertEvidence
{
    /// <summary>The persisted simple import and one composite run per source case.</summary>
    public sealed record EvidenceExample(string ImportedRun, IReadOnlyList<(string CaseId, string Label, string Directory)> Composites);

    /// <summary>Runs against the hand-authored billing fixture and prints the resulting evidence paths.</summary>
    public static async Task RunAsync()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "output", "assert-evidence-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("ASSERT fixture evidence (hand-authored source; no live ASSERT judge or target execution).");
        var evidence = await WriteAsync(AssertInteropRun.FindExampleRun(), output);
        Console.WriteLine($"Direct ASSERT import: {evidence.ImportedRun}");
        foreach (var row in evidence.Composites) Console.WriteLine($"{row.CaseId}: {row.Label} -> {row.Directory}");
        Console.WriteLine("Seals establish stored integrity, not trusted execution. Composite suite identities are legacy rosters, not frozen definitions.");
    }

    /// <summary>Reads saved ASSERT evidence and runs only the local deterministic component; output must be new.</summary>
    public static async Task<EvidenceExample> WriteAsync(string sourceDirectory, string output, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Path.Exists(output)) throw new ArgumentException("Expected: a new output directory. Actual: destination exists. Suggestions: choose a fresh destination. Because: examples must not overwrite evidence.", nameof(output));
        var run = AssertAiRun.Read(sourceDirectory);
        var inputs = AssertAiTranscripts.Read(Path.Combine(sourceDirectory, "inference_set.jsonl")).Select(t => t.Input).ToList();
        inputs.AddRange(run.Missing.Select(m => new EvalInput(string.Empty) { CaseId = m.Key.ToString() }));
        var composite = new CompositeEval("billing_case", "Billing case", "interop", "1.0.0",
            [new EvalComponent(new AssertAiVerdictEval(run)), new EvalComponent(new NoUnconfirmedPayoutChange())], MinAggregation.Instance);
        var caseShape = new AefComponentShape(1, true, "Averaged", composite.Components
            .Select(c => new AefComponentShape(c.Weight, c.Required, c.OnFailure.ToString(), [])).ToArray());
        var envelopeShape = new AefComponentShape(1, true, "Averaged", [caseShape]);
        Directory.CreateDirectory(output);
        var imported = AssertAiImporter.Import(run, Path.Combine(output, "assert-import"),
            new() { ContentCapture = AefContentCapture.On, TargetMode = AefTargetMode.Replayed });
        var rows = new List<(string CaseId, string Label, string Directory)>();
        foreach (var input in inputs.OrderBy(i => i.CaseId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var start = DateTimeOffset.UtcNow;
            var result = await composite.EvaluateAsync(input, ct);
            var key = "source-case-" + (rows.Count + 1);
            result = result with { Metric = result.Metric with { Key = key } };
            // A one-case envelope lets the native writer keep the full composed case tree.
            // It has exactly the observed case verdict, not a newly computed suite gate.
            var envelope = result with { Details = new(null, null, null, [result], "Own") };
            var saved = AefCompositeRunWriter.Write(Path.Combine(output, "composites"), envelope,
                envelopeShape, new()
                {
                    SubjectRef = "agent:billing-fixture", TargetMode = AefTargetMode.Replayed,
                    Lane = "assert-example", SuiteRef = "bench:assert/simple-composite", ContentCapture = AefContentCapture.On,
                    CaseExt = new Dictionary<string, JsonObject> { [key] = new() { ["assert.sourceCase"] = input.CaseId,
                        ["sample.source"] = "hand-authored fixture", ["sample.execution"] = "local deterministic check over saved transcript" } },
                }, start, DateTimeOffset.UtcNow);
            rows.Add((input.CaseId!, result.Score.Label, saved.Directory));
        }
        return new(imported.Directory, rows);
    }
}
