// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Guardrails.Judges;

// Descriptor for the owned configured half of the hybrid gate. Its legacy route remains in HallucinatedCitationJudge.
internal sealed class CitationSupportRubric : IJudgeRubric
{
    public string Axis => "hallucinated-citation";
    public bool Prefilter(string text) => !string.IsNullOrWhiteSpace(text);
    public string BuildPrompt(string text) => throw new NotSupportedException("CitationSupportRubric requires configured prompt execution.");
    public JudgeVerdict Parse(string modelReply) => throw new NotSupportedException("CitationSupportRubric requires the configured decision reader.");
}
