// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;
using Microsoft.Extensions.AI;

namespace AgentEval.Prompts;

/// <summary>One configured execution path shared by the supported RAG metrics.</summary>
internal static class ConfiguredRagJudge
{
    internal static async Task<MetricResult> EvaluateAsync(IChatClient client, string key, EvaluationContext context,
        JudgePromptOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = new JudgePromptResolver().Resolve(BuiltInJudgePrompts.GetRag(key), options);
        var definition = resolved.Definition;
        if (definition.DataSlots.Contains("context") && context.Context is null ||
            definition.DataSlots.Contains("reference") && !ReferenceText.HasWords(context.GroundTruth))
            return MetricResult.NotMeasured(key, NotMeasuredCause.MissingInput,
                "Expected: the instrument's required context/reference. Actual: unavailable evidence. Suggestions: supply the required evidence. Because: missing evidence cannot be graded.");

        var user = JudgePromptRenderer.Render(definition, context.Input, context.Output, context.Context, context.GroundTruth);
        var provenance = JudgePromptRenderer.Provenance(resolved, user);
        List<ChatMessage> messages = [new(ChatRole.System, definition.SystemText), new(ChatRole.User, user)];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (JudgeReplyEvidence.IsCallFailure(ex, cancellationToken))
            {
                return WithProvenance(JudgeReplyEvidence.CallFailed(key, key, ex), provenance);
            }
            if (JudgeResponseReader.TryReadRag(response.Text, definition, out var root, out var score, out var failure))
            {
                var details = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name == "score") continue;
                    details[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Array => property.Value.EnumerateArray().Select(x => x.GetString()!).ToArray(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => property.Value.GetString()!
                    };
                }
                if (details.TryGetValue("informationMissing", out var missing)) details["missingInformation"] = missing;
                return new MetricResult
                {
                    MetricName = key, Score = score, Passed = score / 100 >= definition.PassAt,
                    Explanation = root.GetProperty("reasoning").GetString(), Details = details,
                    ModelGraded = true, RubricDigest = provenance.Digest, JudgePrompt = provenance
                };
            }
            if (attempt == 1) return WithProvenance(JudgeReplyEvidence.NotMeasured(key, key, response, failure), provenance);
            // Reuse original fenced evidence. Do not promote the invalid reply to trusted instructions.
            messages.Add(new(ChatRole.User, definition.RetryText));
        }
        throw new InvalidOperationException("Unreachable retry state.");
    }

    private static MetricResult WithProvenance(MetricResult result, JudgePromptProvenance provenance) => new()
    {
        MetricName = result.MetricName, Score = result.Score, Passed = result.Passed, Measured = result.Measured,
        NotMeasuredCause = result.NotMeasuredCause, Explanation = result.Explanation, Details = result.Details,
        CallFailure = result.CallFailure, JudgePrompt = provenance
    };
}
