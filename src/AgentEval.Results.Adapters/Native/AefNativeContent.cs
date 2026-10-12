// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
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
        List<string> ids = [];
        List<(AefEvidenceKind Kind, string? Text)> items =
        [
            (AefEvidenceKind.Input, content.Input), (AefEvidenceKind.Output, content.Output),
            (AefEvidenceKind.Expected, content.Expected), (AefEvidenceKind.JudgeReasoning, content.JudgeReasoning),
            (AefEvidenceKind.Document, content.Document),
            (AefEvidenceKind.Transcript, content.Transcript),
        ];
        if (content.ToolCalls is { } calls) items.AddRange(calls.Select(c => (AefEvidenceKind.ToolCall, (string?)c)));
        AefBlob? reasoning = line.Reasoning;
        var index = 0;
        foreach (var (kind, text) in items)
        {
            if (text is null) continue;
            var identity = writer.ResultIdOf(line.CaseId, line.Path, line.Trial) + ":" + kind + ":" + index++;
            var id = "E-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var blob = writer.PutBlob(Encoding.UTF8.GetBytes(text));
            if (kind == AefEvidenceKind.JudgeReasoning) reasoning = blob;
            writer.AddEvidence(new AefEvidence
            {
                EvidenceId = id, Kind = kind,
                Link = AefEvidenceLink.ToBlob(blob),
            });
            ids.Add(id);
        }
        return ids.Count == 0 ? line : line with { Evidence = [.. line.Evidence ?? [], .. ids], Reasoning = reasoning };
    }
}
