// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using RuntimeEnforcement = AgentEval.MAF.Gatekeeper.GatekeeperEnforcement;

namespace AgentEval.Samples;

/// <summary>
/// Gatekeeper — secret and oversized tool-result admission, fully offline and deterministic.
///
/// The tool executes before result gates run. The sample therefore asserts the exact promise this boundary can
/// make: a fake credential is masked and excess content is truncated before the result enters model context.
/// A small clean result remains byte-for-byte useful.
/// </summary>
public static class GatekeeperToolResultAdmission
{
    private const int ResultLimit = 180;

    public static async Task RunAsync()
    {
        Console.WriteLine("\n=== Gatekeeper — Tool Result Admission (offline) ===\n");

        await RedactSecretAndTruncateOversizedResultAsync();
        await PreserveCleanResultAsync();

        Console.WriteLine("\n=== Tool Result Admission Complete ===");
    }

    private static async Task RedactSecretAndTruncateOversizedResultAsync()
    {
        Console.WriteLine("① Fake credential + oversized diagnostics — sanitize before model context");

        var fakeToken = "ghp_" + new string('A', 36);
        var rawResult = $"diagnostic-id=demo-42; token={fakeToken}; useful-status=degraded; " + new string('X', 500);
        var executions = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executions);
                return rawResult;
            },
            "download_diagnostics");
        var client = new ScriptedChatClient()
            .AddToolCall("result-1", "download_diagnostics", new Dictionary<string, object?>())
            .AddText("Diagnostics were sanitized and summarized.");
        var trace = new AgentTrace();
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = "ResultAdmissionAttack",
                ChatOptions = new ChatOptions { Tools = [tool], MaxOutputTokens = 256 },
            })
            .AsBuilder()
            .UseGatekeeper(RuntimeEnforcement.ReplaceResult, options =>
            {
                options.Trace = trace;
                // Mask first, then truncate the already-sanitized projection.
                options.AddResultGate(new ToolResultSecretGate());
                options.AddResultGate(new ToolResultSizeGate(ResultLimit));
            })
            .Build();

        await agent.RunAsync("Download and summarize the fake diagnostics.");

        var admitted = SingleFunctionResult(client);
        Require(executions == 1, "the fake diagnostics tool should execute exactly once");
        Require(!admitted.Contains(fakeToken, StringComparison.Ordinal), "the fake token must not enter model context");
        Require(!admitted.Contains("ghp_", StringComparison.Ordinal), "the credential prefix must be masked");
        Require(admitted.Contains('█'), "the secret gate must leave an explicit masked span");
        Require(admitted.Contains("[truncated", StringComparison.Ordinal), "the size gate must add a truncation marker");
        Require(admitted.Length < rawResult.Length, "the admitted projection must be smaller than the raw result");
        Require(HasAction(trace, "tool-result-secret-detection", "Redact"), "secret redaction evidence must be recorded");
        Require(HasAction(trace, "tool-result-size-limit", "Redact"), "size redaction evidence must be recorded");

        Console.WriteLine("   ✅ fake tool executed once; result gates did not pretend to undo that effect");
        Console.WriteLine("   ✅ fake credential masked before the next model turn");
        Console.WriteLine("   ✅ oversized remainder truncated while the useful prefix stayed available");
        GateVoice.Speak(trace, indent: "   ");
    }

    private static async Task PreserveCleanResultAsync()
    {
        Console.WriteLine("\n② Clean bounded diagnostics — preserve utility");

        const string cleanResult = "diagnostic-id=demo-43; useful-status=healthy";
        var executions = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executions);
                return cleanResult;
            },
            "download_clean_diagnostics");
        var client = new ScriptedChatClient()
            .AddToolCall("result-2", "download_clean_diagnostics", new Dictionary<string, object?>())
            .AddText("The clean diagnostics report is healthy.");
        var trace = new AgentTrace();
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = "ResultAdmissionControl",
                ChatOptions = new ChatOptions { Tools = [tool], MaxOutputTokens = 256 },
            })
            .AsBuilder()
            .UseGatekeeper(RuntimeEnforcement.ReplaceResult, options =>
            {
                options.Trace = trace;
                options.AddResultGate(new ToolResultSecretGate());
                options.AddResultGate(new ToolResultSizeGate(ResultLimit));
            })
            .Build();

        await agent.RunAsync("Read the clean fake diagnostics.");

        var admitted = SingleFunctionResult(client);
        Require(executions == 1, "the clean diagnostics tool should execute exactly once");
        Require(string.Equals(admitted, cleanResult, StringComparison.Ordinal), "a clean bounded result must remain unchanged");
        Require(!HasAction(trace, "tool-result-secret-detection", "Redact"), "the clean result must not be secret-redacted");
        Require(!HasAction(trace, "tool-result-size-limit", "Redact"), "the clean result must not be truncated");

        Console.WriteLine("   ✅ clean bounded result reached model context unchanged");
    }

    private static string SingleFunctionResult(ScriptedChatClient client)
    {
        var results = client.ReceivedMessages
            .SelectMany(messages => messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => result.Result?.ToString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Require(results.Length == 1, "exactly one distinct function result should reach the scripted model");
        return results[0];
    }

    private static bool HasAction(AgentTrace trace, string policy, string action)
        => trace.Metadata?.Any(entry =>
            GateMetadataReader.IsGateKey(entry.Key)
            && string.Equals(GateMetadataReader.PolicyFromKey(entry.Key), policy, StringComparison.Ordinal)
            && string.Equals(GateMetadataReader.ReadField(entry.Value, "action"), action, StringComparison.Ordinal)) == true;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Tool-result-admission sample invariant failed: " + message + ".");
        }
    }
}
