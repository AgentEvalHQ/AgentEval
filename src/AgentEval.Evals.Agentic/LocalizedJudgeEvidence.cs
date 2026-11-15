// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentEval.Evals.Agentic;

/// <summary>Structured case facts for configured judges; surrounding instructions belong to their prompt packs.</summary>
internal static class LocalizedJudgeEvidence
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // AtomicLlmEval fences this serialized data when rendering the input/context slot.
    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
