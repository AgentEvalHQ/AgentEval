// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace AgentEval.Core;

/// <summary>
/// The rubrics LLM checks grade with, by prompt id (#203 review, B9). An eval package registers its rubrics when it loads
/// (the agentic evaluators do so in <c>AgenticEvalRegistration</c>); <c>AtomicLlmEval</c> looks its <c>promptId</c> up here
/// and, when its judge is <see cref="IRubricBindable"/>, grades with the rubric.
/// </summary>
public static class EvalRubrics
{
    private static readonly ConcurrentDictionary<string, EvalRubric> s_rubrics = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="rubric"/> under its id. Registering the same id again with identical content is a no-op; different content throws.</summary>
    public static void Register(EvalRubric rubric)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        rubric.Validate();
        var stored = s_rubrics.GetOrAdd(rubric.Id, rubric);
        if (!ReferenceEquals(stored, rubric) && !SameContent(stored, rubric))
            throw new InvalidOperationException($"A different rubric is already registered as '{rubric.Id}'.");
    }

    /// <summary>The rubric registered as <paramref name="id"/>, if any.</summary>
    public static bool TryGet(string? id, out EvalRubric rubric)
    {
        if (id is not null && s_rubrics.TryGetValue(id, out var found))
        {
            rubric = found;
            return true;
        }

        rubric = null!;
        return false;
    }

    /// <summary>Every registered rubric.</summary>
    public static IReadOnlyCollection<EvalRubric> All => s_rubrics.Values.ToArray();

    private static bool SameContent(EvalRubric a, EvalRubric b) =>
        a.Text == b.Text && a.Scale == b.Scale && a.PassAt == b.PassAt && a.ReviewAt == b.ReviewAt
        && a.Dimensional == b.Dimensional && a.SeverityBands.SequenceEqual(b.SeverityBands);
}
