// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Prompts;

namespace AgentEval.Evals;

public sealed partial class AtomicLlmEval
{
    internal bool HasPromptSelection(EvalInput input) => input.JudgePrompts is not null
        || _evaluator is IJudgePromptBindable { HasPromptConfiguration: true };

    private IEvaluator BindPrompts(IJudgePromptBindable bindable, JudgePromptOptions? options)
    {
        var bound = bindable.WithPromptDefinition(CapturePromptDefinition(), options);
        if ((bound as IResolvedJudgePromptSource)?.ResolvedPrompt is null)
            throw new InvalidOperationException("Expected: resolved prompt metadata after binding. Actual: none. Suggestions: implement IResolvedJudgePromptSource. Because: the same policy must drive prompting and scoring.");
        return bound;
    }

    private IEnumerable<string> EvidenceSlots()
    {
        if (Sees.HasFlag(JudgeInputData.ToolCalls)) yield return "toolCalls";
        if (Sees.HasFlag(JudgeInputData.ToolDefinitions)) yield return "toolDefinitions";
        if (Sees.HasFlag(JudgeInputData.SystemMessage)) yield return "systemMessage";
        if (Sees.HasFlag(JudgeInputData.ExpectedActions)) yield return "expectedActions";
        if (Sees.HasFlag(JudgeInputData.EvaluatorNotes)) yield return "evaluatorNotes";
        if (Sees.HasFlag(JudgeInputData.EvaluatorPolicy)) yield return "evaluatorPolicy";
    }

    private void ValidateMetadataSelection(EvalInput input)
    {
        foreach (var (flag, key) in new[]
        {
            (JudgeInputData.EvaluatorNotes, JudgeNotesMetadataKey),
            (JudgeInputData.EvaluatorPolicy, JudgePolicyMetadataKey)
        })
        {
            if (MetadataText(input, key) is not null && !Sees.HasFlag(flag))
                throw new NotSupportedException($"Expected: an explicit {flag} evidence slot. Actual: undeclared evaluator metadata. Suggestions: declare JudgeSeesData.{flag} and export the complete definition. Because: localization must not silently discard evaluator evidence.");
        }
    }

    private EvaluationEvidence CaptureEvidence(EvalInput input) => new()
    {
        ToolCalls = Sees.HasFlag(JudgeInputData.ToolCalls) && input.ToolCalls is { } calls
            ? CallsText(calls) : null,
        ToolDefinitions = Sees.HasFlag(JudgeInputData.ToolDefinitions) && input.ToolDefinitions is { } definitions
            ? BoundedText(definitions, ToolDefinitionJson, d => d.Name, "tool") : null,
        SystemMessage = Sees.HasFlag(JudgeInputData.SystemMessage) && input.SystemMessage is { } system
            ? CutText(system, SystemMessageCharacterLimit) : null,
        ExpectedActions = Sees.HasFlag(JudgeInputData.ExpectedActions) && input.ExpectedActions is { } actions
            ? BoundedText(actions, ExpectedActionJson, a => a.Description, "expected action") : null,
        EvaluatorNotes = Sees.HasFlag(JudgeInputData.EvaluatorNotes) ? MetadataText(input, JudgeNotesMetadataKey) : null,
        EvaluatorPolicy = Sees.HasFlag(JudgeInputData.EvaluatorPolicy) ? MetadataText(input, JudgePolicyMetadataKey) : null,
    };

    private static string? MetadataText(EvalInput input, string key) =>
        input.Metadata?.TryGetValue(key, out var value) == true && value is string text && !string.IsNullOrWhiteSpace(text)
            ? CutText(text, ToolSectionCharacterLimit) : null;

    // Reuse the legacy bounded evidence serializers, including preservation of late destructive
    // calls and explicit truncation diagnostics. Only surrounding instructions belong to the pack.
    private static string CallsText(IReadOnlyList<ToolCall> calls)
    {
        if (calls.Count == 0) return "[]";
        var text = new StringBuilder();
        AppendCalls(text, calls);
        return text.ToString();
    }

    private static string BoundedText<T>(IReadOnlyList<T> items, Func<T, string> line, Func<T, string> name, string noun)
    {
        if (items.Count == 0) return "[]";
        var text = new StringBuilder();
        AppendBounded(text, items, line, name, noun);
        return text.ToString();
    }

    private static string ToolDefinitionJson(ToolDefinition definition) => JsonSerializer.Serialize(new
    {
        name = definition.Name, description = Cut(definition.Description), parameters = Cut(definition.Parameters)
    }, ToolJson);

    private static string ExpectedActionJson(ExpectedAction action) => JsonSerializer.Serialize(new
    {
        description = Cut(action.Description), required_tools = action.RequiredTools
    }, ToolJson);
}
