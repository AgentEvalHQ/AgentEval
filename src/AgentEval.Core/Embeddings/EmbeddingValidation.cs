// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
namespace AgentEval.Embeddings;

/// <summary>Shared validity policy for cached vectors and cosine-based metrics.</summary>
internal static class EmbeddingValidation
{
    internal static string? InvalidReason(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
            return "the embeddings are empty";
        foreach (var component in vector)
        {
            if (!float.IsFinite(component))
                return "an embedding has a component that is not a finite number";
        }

        var norm = System.Numerics.Tensors.TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm))
            return "an embedding's length is too large to compute";
        return norm < 1e-12f ? "an embedding is a zero vector" : null;
    }
}
