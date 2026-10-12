// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Optional observed content. Native writers persist it only with content capture explicitly on.</summary>
public sealed record AefNativeContent
{
    /// <summary>The input actually supplied to the subject.</summary>
    public string? Input { get; init; }
    /// <summary>The subject's observed response.</summary>
    public string? Output { get; init; }
    /// <summary>The expected response supplied by the dataset.</summary>
    public string? Expected { get; init; }
    /// <summary>Observed grading rationale, when available.</summary>
    public string? JudgeReasoning { get; init; }
    /// <summary>Additional evaluation detail, serialized by its producer.</summary>
    public string? Document { get; init; }
    /// <summary>The recorded conversation transcript, when the producer retains one.</summary>
    public string? Transcript { get; init; }
    /// <summary>Individual observed tool calls, serialized without exception objects.</summary>
    public IReadOnlyList<string>? ToolCalls { get; init; }

    internal static AefResult Attach(AefRunWriter writer, AefResult line, AefNativeContent? content)
    {
        if (content is null) return line;
        List<(AefEvidenceKind Kind, string? Text)> items =
        [
            (AefEvidenceKind.Input, content.Input), (AefEvidenceKind.Output, content.Output),
            (AefEvidenceKind.Expected, content.Expected), (AefEvidenceKind.JudgeReasoning, content.JudgeReasoning),
            (AefEvidenceKind.Document, content.Document),
            (AefEvidenceKind.Transcript, content.Transcript),
        ];
        if (content.ToolCalls is { } calls) items.AddRange(calls.Select(c => (AefEvidenceKind.ToolCall, (string?)c)));
        return writer.AttachContent(line, items.Select(item => new AefTextEvidence(item.Kind, item.Text)));
    }
}
