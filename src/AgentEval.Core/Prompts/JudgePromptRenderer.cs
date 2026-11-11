// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Core;

namespace AgentEval.Prompts;

/// <summary>Single-pass substitution for the fixed prompt vocabulary; data never enters the system message.</summary>
internal static class JudgePromptRenderer
{
    internal static string Render(JudgePromptDefinition definition, string? input, string? output,
        string? context = null, string? reference = null, string? dimension = null,
        IReadOnlyList<JudgeCriterion>? criteria = null, EvaluationEvidence? evidence = null)
    {
        return Regex.Replace(definition.UserTemplate, @"\{\{([A-Za-z]+)\}\}", match => match.Groups[1].Value switch
        {
            "input" => PromptSafety.Fence(input),
            "output" => PromptSafety.Fence(output),
            "context" => PromptSafety.Fence(context),
            "reference" => PromptSafety.Fence(reference),
            "toolCalls" => PromptSafety.Fence(evidence?.ToolCalls),
            "toolDefinitions" => PromptSafety.Fence(evidence?.ToolDefinitions),
            "systemMessage" => PromptSafety.Fence(evidence?.SystemMessage),
            "expectedActions" => PromptSafety.Fence(evidence?.ExpectedActions),
            "evaluatorNotes" => PromptSafety.Fence(evidence?.EvaluatorNotes),
            "evaluatorPolicy" => PromptSafety.Fence(evidence?.EvaluatorPolicy),
            "dimension" => PromptSafety.Fence(dimension),
            "criteria" => JsonSerializer.Serialize((criteria ?? definition.Criteria).Select(c => new { id = c.Id, text = c.Text })),
            "safety" => definition.SafetyText,
            _ => throw new InvalidOperationException("Expected: a validated template. Actual: unknown slot. Suggestions: validate the definition. Because: templates cannot execute expressions.")
        }, RegexOptions.CultureInvariant);
    }

    internal static JudgePromptProvenance Provenance(ResolvedJudgePrompt resolved, string userMessage) =>
        resolved.Provenance with
        {
            Content = resolved.CaptureContent
                ? new(resolved.Definition.SystemText, userMessage, resolved.Definition.RetryText)
                : null
        };
}
