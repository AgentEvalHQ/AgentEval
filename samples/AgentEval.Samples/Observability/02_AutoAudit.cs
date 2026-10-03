// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Auto-Audit (Observability). Audits the configured models: each runs one support task whose tool returns
/// a record with a test SSN the instructions forbid repeating, captured at the chat boundary and at the tool loop's own
/// response, then ranked on honesty (Trace Fidelity), safety (gate blocks) and cost (tokens, latency).
/// </summary>
/// <remarks>
/// The models are the provider's <c>*_MODEL</c>, <c>*_MODEL_2</c> and <c>*_MODEL_3</c>; one call chain per model, a few
/// thousand tokens each. With no provider the sample stops. <c>--mock</c> shows the scripted showcase instead, labelled
/// MOCK: three made-up endpoints that measure no model.
/// </remarks>
public static class AutoAudit
{
    /// <summary>Runs the auto-audit.</summary>
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Auto-Audit ===\n");
        if (!AIConfig.StartModelSample())
            return;

        AutoAuditReport report;
        if (AIConfig.UseMock)
        {
            report = await AutoAuditDemo.BuildOfflineReportAsync();
        }
        else
        {
            // Only the models the environment names; the provider's fallback second and third models are defaults
            // nobody configured (for Azure, deployments that may not exist).
            var models = AgentEval.Providers.InferenceProviderEnvironment.NamedModels(
                AIConfig.Settings, Environment.GetEnvironmentVariable);
            var results = new List<AutoAuditEndpointResult>();
            foreach (var model in models)
            {
                var endpoint = $"{model} ({AIConfig.ProviderTag})";
                Console.WriteLine($"  ⏳ auditing {endpoint} ...");
                using var client = AIConfig.CreateChatClient(model);
                var (result, failure) = await AutoAuditLive.EvaluateAsync(endpoint, client);
                if (failure is not null)
                    Console.WriteLine($"     did not complete: {failure.GetType().Name}: {failure.Message}");
                results.Add(result);
            }
            report = AutoAuditRunner.Compare(results);
            Console.WriteLine();
        }

        foreach (var r in report.Ranking)
        {
            if (!r.Completed)
            {
                Console.WriteLine($"  {r.Endpoint,-32} did not complete the task: not measured{AIConfig.MockLabel}");
                continue;
            }
            var top = r.TopDiscrepancies.Count > 0 ? string.Join(", ", r.TopDiscrepancies) : "none";
            var leak = r.LeakedPastGate ? "  LEAKED the SSN past the gate" : "";
            Console.WriteLine($"  {r.Endpoint,-32} fidelity={r.FidelityScore * 100,3:F0}%  gateBlocks={r.GateBlocks}{leak}  tokens={r.TotalTokens}  discrepancies: {top}{AIConfig.MockLabel}");
        }

        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine(AIConfig.UseMock
            ? "MOCK: the endpoints above are scripted. Run without --mock to audit your configured models."
            : "One command, several models, ranked on honesty + safety + cost: the Glass Box flagship.");
    }
}
