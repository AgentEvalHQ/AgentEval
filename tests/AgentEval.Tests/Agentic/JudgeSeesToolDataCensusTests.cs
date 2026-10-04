// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// The judge sees the tool data its rubric names (#203 review, B5b). <c>AtomicLlmEval</c> sent the judge the query,
/// context and response only, so the 14 evaluators whose rubric lists tool calls (<c>unsafe-tool-use</c>: "the primary
/// input") graded tool use they were never shown. This census is DRIVEN BY THE RUBRIC FILES the package ships — not by
/// a list in this file — so a rubric that starts naming tool data, or an evaluator that stops sending it, fails here.
/// It checks both directions: the evaluators that send tool data are exactly those whose rubric names it.
/// </summary>
public class JudgeSeesToolDataCensusTests
{
    private const string CanaryTool = "canary_tool_7f3a";
    private const string CanaryArgument = "canary-argument-7f3a";
    private const string CanaryResult = "canary-result-7f3a";
    private const string CanaryDefinition = "canary-definition-7f3a";

    /// <summary>A judge that records every input it is given. Costs nothing and reaches no network.</summary>
    private sealed class InputRecordingJudge : IEvaluator
    {
        private readonly List<string> _inputs = [];

        public IReadOnlyList<string> Inputs => _inputs;

        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            _inputs.Add(input);
            return Task.FromResult(new EvaluationResult { OverallScore = 42, Summary = "recording-stub" });
        }
    }

    private sealed record Rubric(string Key, bool NamesCalls, bool NamesDefinitions);

    // The input section of every shipped rubric: "## Inputs" or "## Input Format", up to the next "## " heading.
    private static IReadOnlyList<Rubric> ShippedRubrics()
    {
        var assembly = typeof(UnsafeToolUseEval).Assembly;
        var rubrics = new List<Rubric>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Resources.Prompts.", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var text = new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var section = Regex.Match(text, @"^##+ *Input[^\n]*\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var body = section.Success ? section.Groups[1].Value : string.Empty;

            // "…Prompts.safety.unsafe-tool-use.v1.md" → "unsafe_tool_use"
            var file = Regex.Match(name, @"\.([a-z0-9-]+)\.v\d+\.md$").Groups[1].Value;
            rubrics.Add(new Rubric(
                file.Replace('-', '_'),
                Regex.IsMatch(body, @"tool_calls|tool calls", RegexOptions.IgnoreCase),
                Regex.IsMatch(body, @"tool_definitions|tool definitions", RegexOptions.IgnoreCase)));
        }

        return rubrics;
    }

    private static EvalRegistry Registry()
    {
        var registry = new EvalRegistry();
        AgenticEvalRegistration.RegisterInto(registry);
        return registry;
    }

    // A tool call with no recorded outcome and a plain-text result, so the code-first evaluators (tool_call_success,
    // prohibited_actions, sensitive_data_leakage) reach their judge; no PII, no policy match.
    private static EvalInput CanaryInput() => new(
        Query: "Look up the order status for order 1234.",
        Response: "Your order 1234 has shipped.",
        Context: "Order 1234 shipped on Monday.",
        ToolCalls: [new ToolCall(CanaryTool, new Dictionary<string, object> { ["order"] = CanaryArgument }, CanaryResult)],
        ToolDefinitions: [new ToolDefinition(CanaryTool, CanaryDefinition, new Dictionary<string, object> { ["required"] = new object[] { "order" } })],
        ExpectedActions: [new ExpectedAction("look up the order", [CanaryTool])]);

    // Evaluators that ship with a rubric but are not in the key registry, built the way their callers build them:
    //   prohibited_actions — needs an IPolicyResolver and a subject id, so the registry cannot dispatch it (see
    //     AgenticEvalRegistration); an empty policy finds no violation in code, so the judge fallback runs.
    //   intermediate_step_hallucination — a trace-dependent reasoning evaluator, carved out of calibration and built
    //     directly by the agentic benchmark.
    private static readonly Dictionary<string, Func<IEvaluator, IEval>> BuiltDirectly = new(StringComparer.Ordinal)
    {
        ["prohibited_actions"] = judge => new ProhibitedActionsEval(judge, new EmptyPolicy(), "census-subject"),
        ["intermediate_step_hallucination"] = judge => new AgentEval.Evals.Agentic.Reasoning.IntermediateStepHallucinationEval(judge),
    };

    private sealed class EmptyPolicy : AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver
    {
        public AgentEval.Evals.Agentic.Safety.Policy.ProhibitedActionPolicy GetPolicyFor(string subjectId) => new([], [], [], []);
    }

    private static IEval? Build(EvalRegistry registry, string key, IEvaluator judge) =>
        registry.Resolve(key, judge, judgeModel: null)
        ?? (BuiltDirectly.TryGetValue(key, out var build) ? build(judge) : null);

    private static async Task<IReadOnlyList<string>> JudgeInputsAsync(EvalRegistry registry, string key)
    {
        var judge = new InputRecordingJudge();
        var eval = Build(registry, key, judge);
        Assert.True(eval is not null, $"no evaluator for '{key}'");
        await eval!.EvaluateAsync(CanaryInput());
        return judge.Inputs;
    }

    [Fact]
    public void TheRubricsThatNameToolData_AreFound_AndEachMatchesAnEvaluator()
    {
        var registry = Registry();
        var naming = ShippedRubrics().Where(r => r.NamesCalls || r.NamesDefinitions).ToList();

        // 14 on 2026-10-04. A drop means the parse stopped finding them, not that the rubrics changed.
        Assert.True(naming.Count >= 14, $"only {naming.Count} rubrics found naming tool data");
        var unmatched = naming.Where(r => Build(registry, r.Key, new InputRecordingJudge()) is null).Select(r => r.Key).ToList();
        Assert.True(unmatched.Count == 0, "rubrics with no evaluator: " + string.Join(", ", unmatched));
    }

    [Fact]
    public async Task EveryEvaluatorWhoseRubricNamesToolCalls_ShowsItsJudgeTheToolCalls()
    {
        var registry = Registry();
        var blind = new List<string>();

        foreach (var rubric in ShippedRubrics().Where(r => r.NamesCalls))
        {
            var inputs = await JudgeInputsAsync(registry, rubric.Key);
            var shown = inputs.Any(i => i.Contains(CanaryTool, StringComparison.Ordinal)
                                        && i.Contains(CanaryResult, StringComparison.Ordinal));
            if (!shown)
                blind.Add($"{rubric.Key} (judge calls: {inputs.Count})");
        }

        Assert.True(blind.Count == 0, "judges asked about tool calls they are not shown: " + string.Join("; ", blind));
    }

    [Fact]
    public async Task EveryEvaluatorWhoseRubricNamesToolDefinitions_ShowsItsJudgeTheDefinitions()
    {
        var registry = Registry();
        var blind = new List<string>();

        foreach (var rubric in ShippedRubrics().Where(r => r.NamesDefinitions))
        {
            var inputs = await JudgeInputsAsync(registry, rubric.Key);
            if (!inputs.Any(i => i.Contains(CanaryDefinition, StringComparison.Ordinal)))
                blind.Add($"{rubric.Key} (judge calls: {inputs.Count})");
        }

        Assert.True(blind.Count == 0, "judges asked about tool definitions they are not shown: " + string.Join("; ", blind));
    }

    /// <summary>Records each judge call's criteria with its input, so a LEAF can be told from its evaluator.</summary>
    private sealed class CriteriaRecordingJudge : IEvaluator
    {
        public List<(IReadOnlyList<string> Criteria, string Input)> Calls { get; } = [];

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Calls.Add((criteria.ToList(), input));
            return Task.FromResult(new EvaluationResult { OverallScore = 42, Summary = "recording-stub" });
        }
    }

    [Theory]
    [InlineData("authorization boundaries", true)]   // the motivating leaf (an unauthorized action)
    [InlineData("system rules specified", true)]
    [InlineData("prescribed procedure", true)]
    [InlineData("user's stated goal", false)]        // grades the answer text
    [InlineData("format, tone, and style", false)]
    public async Task TaskAdherence_EachLeaf_SeesTheToolCallsExactlyWhenItGradesActions(string criterionPhrase, bool seesToolCalls)
    {
        // B6c-11 (mid-branch review): the census above asks whether ANY of an evaluator's judge calls saw the tool calls,
        // so removing the authorization leaf's opt-in stayed green. This pins it per leaf.
        var judge = new CriteriaRecordingJudge();
        await new AgentEval.Evals.Agentic.System.TaskAdherenceEval(judge).EvaluateAsync(CanaryInput());

        var call = Assert.Single(judge.Calls, c => c.Criteria.Any(x => x.Contains(criterionPhrase, StringComparison.Ordinal)));
        Assert.Equal(seesToolCalls, call.Input.Contains(CanaryTool, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoOtherJudge_IsSentToolData()
    {
        // The other direction: tool data costs tokens and can bias a judge grading something else (tone, fluency), so
        // it goes only where the rubric asks for it. Judged over the evaluators that HAVE a rubric which names no tool
        // data; an evaluator without a rubric (an aggregate such as tool_call_accuracy, which nests tool_input_accuracy)
        // has nothing to be held to.
        var registry = Registry();
        var silent = ShippedRubrics().Where(r => !r.NamesCalls && !r.NamesDefinitions).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        Assert.True(silent.Count >= 20, $"only {silent.Count} rubrics found that name no tool data");
        var leaking = new List<string>();

        foreach (var key in registry.All.Select(r => r.Key).Where(silent.Contains))
        {
            var inputs = await JudgeInputsAsync(registry, key);
            if (inputs.Any(i => i.Contains(CanaryResult, StringComparison.Ordinal) || i.Contains(CanaryDefinition, StringComparison.Ordinal)))
                leaking.Add(key);
        }

        Assert.True(leaking.Count == 0, "judges sent tool data their rubric does not name: " + string.Join(", ", leaking));
    }
}
