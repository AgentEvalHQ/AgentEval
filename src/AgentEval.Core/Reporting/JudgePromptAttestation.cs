// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Collections.ObjectModel;
using AgentEval.Evals;

namespace AgentEval.Core.Reporting;

/// <summary>Content-free observed prompt identities for existing report attestation maps.</summary>
public static class JudgePromptAttestation
{
    /// <summary>Reads actual leaf provenance, preserving every distinct instrument used for a result key.</summary>
    /// <param name="result">The completed evaluation tree; no judge is invoked.</param>
    /// <returns>
    /// Null when no configured prompt was recorded, allowing legacy report options to retain their behavior.
    /// Otherwise, a result-key map containing evaluator key, instrument ID, version, effective locale and digest.
    /// Repeated identities are deduplicated; mixed identities and unidentified legacy leaves are explicitly flagged.
    /// Captured system, user and retry text are never included.
    /// </returns>
    public static IReadOnlyDictionary<string, string>? FromResult(EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var observed = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(EvalResult Node, int Depth)>();
        pending.Push((result, 0));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > EvalTreeLimits.MaxTreeWalkDepth)
                throw new ArgumentException("Expected: a bounded evaluation tree. Actual: maximum attestation depth exceeded. Suggestions: remove cycles or reduce nesting. Because: instrument provenance must not be silently omitted.", nameof(result));
            if (current.Node.Details.SubResults is { Count: > 0 } children)
            {
                foreach (var child in children) pending.Push((child, current.Depth + 1));
                continue;
            }

            var provenance = current.Node.Provenance;
            if (provenance.JudgePrompt is { } prompt)
                Add(current.Node.Metric.Key,
                    $"evaluator={prompt.EvaluatorKey}; instrument={prompt.InstrumentId}; version={prompt.Version}; locale={prompt.EffectiveLocale}; digest={prompt.Digest}");
            else if (provenance.Type == "atomic-llm" || provenance.JudgeModel is not null || provenance.PromptId is not null)
                unresolved.Add(current.Node.Metric.Key);
        }

        if (observed.Count == 0) return null;
        foreach (var key in unresolved)
            Add(key, "UNIDENTIFIED legacy instrument: version, locale and full identity were not recorded");
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, identities) in observed)
            versions.Add(key, (identities.Count > 1 ? "MIXED observed instruments: " : "") + string.Join(" | ", identities));
        return new ReadOnlyDictionary<string, string>(versions);

        void Add(string key, string identity)
        {
            if (!observed.TryGetValue(key, out var identities)) observed.Add(key, identities = new(StringComparer.Ordinal));
            identities.Add(identity);
        }
    }
}
