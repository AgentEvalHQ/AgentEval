// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001 // Sample intentionally demonstrates the preview anomaly gate.

using AgentEval.MAF.Gatekeeper;

namespace AgentEval.Samples;

/// <summary>Offline fixed-limit versus per-tool running result-anomaly demonstration.</summary>
public static class GatekeeperToolResultBehavioralAnomaly
{
    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("29");
        Console.WriteLine("\n=== Gatekeeper — Tool Result Behavioral Anomaly (offline) ===\n");

        var fixedLimit = new ToolResultSizeGate(maxLength: 5000);
        var fixedFile = await fixedLimit.InspectAsync(Result("read_large_file", 1200));
        var fixedLookupSpike = await fixedLimit.InspectAsync(Result("lookup_customer", 500));
        Require(
            fixedFile.Action == ToolResultAction.Allow &&
            fixedLookupSpike.Action == ToolResultAction.Allow,
            "the global 5000-character cap must honestly allow both below-cap results");

        var anomaly = new ToolResultSizeAnomalyGate(
            anomalyMultiplier: 5.0,
            minBaselineCalls: 3,
            minFlagSize: 100);
        ToolResultVerdict normalFile;
        ToolResultVerdict lookupSpike;
        ToolResultVerdict repeatedSpike;
        using (AgentRunScope.Begin(session: null, "result-anomaly-sample", trace: null))
        {
            for (var i = 0; i < 3; i++)
            {
                Require(
                    (await anomaly.InspectAsync(Result("read_large_file", 1000))).Action ==
                    ToolResultAction.Allow,
                    "routine file results must establish their own large baseline");
                Require(
                    (await anomaly.InspectAsync(Result("lookup_customer", 40))).Action ==
                    ToolResultAction.Allow,
                    "routine lookup results must establish their own small baseline");
            }

            normalFile = await anomaly.InspectAsync(Result("read_large_file", 1200));
            lookupSpike = await anomaly.InspectAsync(Result("lookup_customer", 500));
            repeatedSpike = await anomaly.InspectAsync(Result("lookup_customer", 500));
        }

        Require(normalFile.Action == ToolResultAction.Allow,
            "a routinely large file result must remain useful against its own baseline");
        Require(
            lookupSpike.Action == ToolResultAction.Redact &&
            repeatedSpike.Action == ToolResultAction.Redact,
            "the lookup outlier and its repeat must redact without poisoning the baseline");

        ToolResultVerdict freshRun;
        using (AgentRunScope.Begin(session: null, "result-anomaly-sample", trace: null))
        {
            freshRun = await anomaly.InspectAsync(Result("lookup_customer", 500));
        }

        Require(freshRun.Action == ToolResultAction.Allow,
            "a new run must start with no per-tool anomaly baseline");

        Console.WriteLine($"   fixed 5000-char cap:  read_large_file(1200)={fixedFile.Action}, lookup_customer(500)={fixedLookupSpike.Action} — a global cap cannot see per-tool norms");
        Console.WriteLine($"   per-tool baseline:    routine large file={normalFile.Action}; the same 500 chars from lookup_customer={lookupSpike.Action}");
        Console.WriteLine($"   baseline integrity:   repeated spike={repeatedSpike.Action} (a flagged result is never learned into its own baseline)");
        Console.WriteLine($"   next run:             first lookup={freshRun.Action} (per-run baselines reset)");
        Console.WriteLine("   ✅ fixed exhaustion limits and behavioral drift detection remained distinct.");
    }

    private static GatedToolResult Result(string tool, int size) => new(
        FunctionName: tool,
        Arguments: null,
        Result: new string('x', size),
        AgentName: "result-anomaly-sample",
        Iteration: 0,
        FunctionCallIndex: 0,
        FunctionCount: 1,
        IsStreaming: false,
        Messages: null);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Result-anomaly sample failed: " + message + ".");
        }
    }
}
