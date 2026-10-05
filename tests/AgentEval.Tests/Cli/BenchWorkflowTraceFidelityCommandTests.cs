// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Tracing;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Glass Box Phase 3 (P3.2b) — <c>agenteval bench workflow-trace-fidelity</c>. Pure-code (no Azure):
/// exercises the load → replay → reconcile → persist path and the 0/2/1 exit-code contract.
/// </summary>
public class BenchWorkflowTraceFidelityCommandTests : IDisposable
{
    private readonly string _root;

    public BenchWorkflowTraceFidelityCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-workflow-trace-fidelity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "WorkflowTraceFidelityBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> WriteTraceAsync(WorkflowTrace trace)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".trace.json");
        await WorkflowTraceSerializer.SaveToFileAsync(trace, path);
        return path;
    }

    private static WorkflowTraceStep Step(string id, int prompt, int completion, string? finish) =>
        new()
        {
            ExecutorId = id,
            Output = id,
            StepIndex = 0,
            TokenUsage = new TraceTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
            FinishReason = finish,
        };

    private static AgentTrace ChatTrace(int totalTokens, string? finish)
    {
        var t = new AgentTrace();
        t.Entries.Add(TraceEntry.ForChatResponse(0, null, "r", 1,
            new TraceTokenUsage { PromptTokens = totalTokens, CompletionTokens = 0 }, null, finish, null));
        return t;
    }

    [Fact]
    public async Task MissingTraceFile_ReturnsExitCode1()
    {
        var code = await BenchWorkflowTraceFidelityCommand.RunAsync(
            Path.Combine(_root, "does-not-exist.trace.json"), "standard", "wf", _root);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task NoExecutorTraces_AllNoTruth_ReturnsExitCode0()
    {
        var trace = new WorkflowTrace
        {
            TraceName = "wtf", OriginalPrompt = "go", FinalOutput = "done",
            Steps = { Step("a", 10, 5, "stop") },
        };
        var path = await WriteTraceAsync(trace);

        var code = await BenchWorkflowTraceFidelityCommand.RunAsync(path, "standard", "wf", _root);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task AWarnScore_ExitsTen_NotZero()
    {
        // Review round 5 M-2 (B10w): five executors agree, one's tokens differ: (5 + 0.5) / 6 = 0.917, labelled warn. The
        // command decided from Passed (true at >= 0.80), printed and stored PASS and exited 0; every other bench exits 10.
        var trace = new WorkflowTrace { TraceName = "wtf", OriginalPrompt = "go", FinalOutput = "done" };
        var executors = new Dictionary<string, AgentTrace>();
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" })
        {
            trace.Steps.Add(Step(id, 10, 5, "stop"));                         // framework: 15 tokens
            executors[id] = ChatTrace(id == "f" ? 99 : 15, "stop");           // chat truth: 15, except f
        }
        trace.ExecutorTraces = executors;
        var path = await WriteTraceAsync(trace);

        var code = await BenchWorkflowTraceFidelityCommand.RunAsync(path, "standard", "wf", _root);

        Assert.Equal(10, code);   // GateWarning, as BenchExitCodes.FromLabel("warn")
    }

    [Fact]
    public async Task TokenMismatch_ReturnsExitCode9()
    {
        var trace = new WorkflowTrace
        {
            TraceName = "wtf", OriginalPrompt = "go", FinalOutput = "done",
            Steps = { Step("a", 10, 5, "stop") },                          // framework: 15 tokens
            ExecutorTraces = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(99, "stop") }, // chat truth: 99
        };
        var path = await WriteTraceAsync(trace);

        var code = await BenchWorkflowTraceFidelityCommand.RunAsync(path, "standard", "wf", _root);

        Assert.Equal(9, code); // TokenMismatch → score 0.5 < 0.8 → FAIL (GateFailed)
    }
}
