// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Store-v1 run identities belonging to a native benchmark invocation; never filesystem paths.</summary>
public sealed record AefStoreV1Link
{
    /// <summary>The single run or persisted aggregate, when one was created.</summary>
    public string? RunId { get; init; }

    /// <summary>Persisted trial run IDs, in execution order, including interrupted invocations.</summary>
    public IReadOnlyList<string>? TrialRunIds { get; init; }

    internal JsonObject ToExtension()
    {
        if (RunId is null && (TrialRunIds is null || TrialRunIds.Count == 0))
            throw new ArgumentException("A store-v1 link requires an observed run identity.");
        static void Validate(string id)
        {
            if (string.IsNullOrEmpty(id) || !char.IsAsciiLetterOrDigit(id[0]) ||
                id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
                throw new ArgumentException("A store-v1 run identity must be a path-free identifier.");
        }
        var link = new JsonObject();
        if (RunId is not null) { Validate(RunId); link["runId"] = RunId; }
        if (TrialRunIds is { Count: > 0 })
        {
            var ids = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in TrialRunIds)
            {
                Validate(id);
                if (!seen.Add(id)) throw new ArgumentException("Store-v1 trial run identities must be distinct.");
                ids.Add(id);
            }
            link["trialRunIds"] = ids;
        }
        return new JsonObject { ["agenteval.store-v1"] = link };
    }
}
