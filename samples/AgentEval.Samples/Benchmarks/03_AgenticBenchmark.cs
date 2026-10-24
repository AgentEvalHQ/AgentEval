// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Models;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H3: Agentic — invokes a real model-backed agent that has a tool, records the tool calls it makes,
/// and grades its answer and those calls with the agentic preset selected by
/// <see cref="BenchmarkSampleHelpers.ResolvePreset"/>. Renders the resulting composite tree to JSON + HTML + PDF
/// using the v0.10.1 generic renderers.
///
/// Preset mapping:
/// <list type="bullet">
///   <item><see cref="SamplePreset.Smoke"/> → <see cref="AgenticBenchmark.ToolCallAccuracy"/></item>
///   <item><see cref="SamplePreset.Standard"/> → <see cref="AgenticBenchmark.AgenticExecution"/></item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <see cref="AgenticBenchmark.AgenticExecution"/> (same 6-sub-eval composite; audit teams typically pair this with a stronger judge model)</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Requires a model provider (see AIConfig). Skips gracefully when missing.
/// For multi-prompt JSONL-driven runs see <c>samples/DataAndInfrastructure/04_BenchmarkSystem.cs</c>.
/// </para>
/// <para>
/// The agent has one local weather tool with fixed data (a fixture, not a live service), and the calls it makes go
/// into the <see cref="EvalInput"/> with the tool's definition and the expected call. Until 2026-10 the agent had no
/// tools and the input carried only the query and the reply, so both presets graded tool selection, tool inputs and
/// tool success on a run that had none to grade: the same wiring fault H12 had.
/// </para>
/// </remarks>
public static class AgenticBenchmarkSample
{
    private const string WeatherTool = "get_current_weather";

    private const string Instructions =
        "You are a helpful assistant that uses tools when needed. Keep replies tight.";

    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H3: Agentic",
            "Real agent invocation + real judge. JSON + HTML + PDF outputs.");

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H3 — AGENTIC");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        var judge = BenchmarkSampleHelpers.CreateAzureJudge();
        var (composite, presetLabel) = preset switch
        {
            SamplePreset.Standard => (
                AgenticBenchmark.AgenticExecution(judge, judgeModel: AIConfig.ModelDeployment),
                "Agentic-Execution (6 sub-evals)"),
            SamplePreset.AuditGrade => (
                AgenticBenchmark.AgenticExecution(judge, judgeModel: AIConfig.ModelDeployment),
                "Agentic-Execution (audit-grade; pair with a stronger judge model)"),
            _ => (
                AgenticBenchmark.ToolCallAccuracy(judge, judgeModel: AIConfig.ModelDeployment),
                "Tool-Call-Accuracy (5 sub-evals)"),
        };

        // The agent under test has the tool the question needs; both presets grade the calls it makes with it.
        AITool[] tools = [AIFunctionFactory.Create(GetCurrentWeather, WeatherTool)];
        AIAgent agent = AIConfig.CreateChatClient(AIConfig.ModelDeployment).AsAIAgent(
            name: "AgenticSampleAgent",
            instructions: Instructions,
            tools: tools);

        const string query = "What's the weather in Paris right now? If you can't look it up, say so plainly.";
        Console.WriteLine($"Invoking real agent: \"{query}\"");
        var runResponse = await agent.RunAsync(query);
        Console.WriteLine($"Agent replied ({runResponse.Text?.Length ?? 0} chars).");

        // The calls the agent made, as the run recorded them: an empty list when it called nothing, which the tool
        // evaluators grade as "no tool was called", not as "no tool data". The expected call becomes the input's
        // expected action, which Agentic-Execution's navigation-efficiency leaf compares the calls against.
        var testCase = new TestCase { Name = "paris-weather-now", Input = query, ExpectedTools = [WeatherTool] };
        var run = new TestResult
        {
            TestName = testCase.Name,
            ActualOutput = runResponse.Text,
            ToolUsage = ToolUsageExtractor.Extract(runResponse.Messages.Cast<object>().ToList()),
        };
        var input = testCase.ToEvalInput(run) with
        {
            ToolDefinitions = tools.OfType<AIFunction>().Select(BenchmarkSampleHelpers.ToToolDefinition).ToList(),
            SystemMessage = Instructions,
        };
        Console.WriteLine($"Tool calls the agent made: {BenchmarkSampleHelpers.DescribeToolCalls(input)}  (offered: {WeatherTool}; fixed fixture data)");
        Console.WriteLine($"Grading with preset: {presetLabel}");

        var result = await composite.EvaluateAsync(input);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: agent.Name ?? "AgenticSampleAgent",
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
            result, subject,
            benchmarkName: "agentic",
            regulationOrBenchmark: $"AgentEval Agentic — {presetLabel}",
            includePdf: true,
            regulationCodeForEvidence: null,
            presetLabel: preset.ToString().ToLowerInvariant(),
            judgeModel: AIConfig.ModelDeployment);

        BenchmarkSampleHelpers.PrintReportPaths(result, paths);
        SampleOutcome.RecordVerdict("agentic benchmark", result);
        BenchmarkSampleHelpers.OfferToOpenReports(paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - The agent really ran — the EvalResult grades the live response and the tool calls it made, not a stub.");
        Console.WriteLine("   - A tool-accuracy eval needs the run's tool calls in its input: no calls in, nothing graded.");
        Console.WriteLine("   - Smoke → Tool-Call-Accuracy preset; Standard / Audit-Grade → Agentic-Execution.");
        Console.WriteLine("   - For multi-prompt JSONL-driven runs see DataAndInfrastructure/04_BenchmarkSystem.cs.");
    }

    [Description("Gets the current weather for a city.")]
    private static string GetCurrentWeather([Description("The city, e.g. Paris")] string city)
    {
        if (!city.Contains("Paris", StringComparison.OrdinalIgnoreCase))
            return $"No weather data available for {city}.";

        return "Paris: 17°C, light rain, wind 12 km/h from the south-west, humidity 82%.";
    }
}
