// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Prompts;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

internal sealed class ConfiguredGateJudge
{
    private readonly ResolvedJudgePrompt _resolved;
    private readonly JudgeGateOptions _options;
    private readonly IChatClient _client;
    internal string Digest { get; }
    internal double BlockThreshold => _resolved.Definition.PassAt;

    internal ConfiguredGateJudge(IJudgeRubric rubric, IChatClient client, JudgeGateOptions options, string? evidencePolicy = null)
    {
        if (options.MaxOutputTokens <= 0 || options.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature < 0) ||
            options.ModelId is { } model && (string.IsNullOrWhiteSpace(model) || model.Length > 512))
            throw new ArgumentException("Expected: valid configured model settings. Actual: invalid output limit, temperature or model ID. Suggestions: correct JudgeGateOptions. Because: invalid settings cannot identify an executable instrument.", nameof(options));
        _resolved = new JudgePromptResolver().Resolve(GateJudgePrompts.ForRubric(rubric, options.BlockThreshold), options.JudgePrompts!);
        _options = options;
        _client = client;
        Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            rubric.Axis, prompt = _resolved.Provenance.Digest, reader = "gate.decision-json.v1",
            prefilter = "bypass-semantic.v1", truncation = "head-tail.v1", retry = "one-correction-shared-deadline.v1",
            options.ModelId, options.Temperature, options.MaxOutputTokens, options.MaxInputChars,
            timeoutTicks = options.Timeout.Ticks, options.FailClosedOnInconclusive, options.FailClosedOnBudgetExhausted,
            governor = options.SpendGovernor?.Configuration
        }))).ToLowerInvariant();
        if (evidencePolicy is not null)
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { instrument = Digest, evidencePolicy }))).ToLowerInvariant();
    }

    internal async ValueTask<JudgeVerdict> EvaluateAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = _resolved.Definition;
        var user = JudgePromptRenderer.Render(definition, text, string.Empty, null);
        var provenance = JudgePromptRenderer.Provenance(_resolved, user);
        var calls = new List<JudgeGateCall>();
        if (string.IsNullOrWhiteSpace(text)) return Attach(JudgeVerdict.Inconclusive("Empty input cannot establish a configured judge decision."));
        var messages = new List<ChatMessage> { new(ChatRole.System, definition.SystemText), new(ChatRole.User, user) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.Timeout);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Each actual attempt reserves the complete rendered request plus its output allowance.
                var estimatedTokens = messages.Sum(m => (long)(m.Text?.Length ?? 0) + 3) / 4 + _options.MaxOutputTokens;
                if (_options.SpendGovernor is { } governor && !governor.TryReserve(estimatedTokens))
                    return Attach(JudgeVerdict.Inconclusive("Configured judge unmeasured: spend budget exhausted."), budgetExhausted: true);
                var index = calls.Count;
                calls.Add(new(null, false));
                var reply = await _client.GetResponseAsync(messages, new ChatOptions
                {
                    ModelId = _options.ModelId, Temperature = _options.Temperature, MaxOutputTokens = _options.MaxOutputTokens
                }, deadline.Token).ConfigureAwait(false);
                calls[index] = new(string.IsNullOrWhiteSpace(reply.ModelId) ? null : reply.ModelId, true);
                deadline.Token.ThrowIfCancellationRequested();
                if (TryRead(reply.Text, out var verdict)) return Attach(verdict);
                if (attempt == 0) messages.Add(new(ChatRole.User, definition.RetryText));
            }
            return Attach(JudgeVerdict.Inconclusive("Configured judge reply remained invalid after one corrective retry."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Attach(JudgeVerdict.Inconclusive("Configured judge deadline expired.")); }
        catch (Exception ex) { return Attach(JudgeVerdict.Inconclusive($"Configured judge call failed ({ex.GetType().Name}).")); }

        JudgeVerdict Attach(JudgeVerdict result, bool budgetExhausted = false) => result with
        {
            Instrument = new(Digest, provenance, _options.ModelId, calls.AsReadOnly(), result.Decision != JudgeDecision.Inconclusive, budgetExhausted)
        };
    }

    // Offline reply parsing shares the exact reader and records zero dispatches, never a fabricated model observation.
    internal JudgeVerdict ReadReply(string text, string reply)
    {
        var user = JudgePromptRenderer.Render(_resolved.Definition, text, string.Empty, null);
        var verdict = !string.IsNullOrWhiteSpace(text) && TryRead(reply, out var read)
            ? read : JudgeVerdict.Inconclusive("Configured offline reply is invalid or input is empty.");
        return verdict with { Instrument = new(Digest, JudgePromptRenderer.Provenance(_resolved, user),
            _options.ModelId, [], verdict.Decision != JudgeDecision.Inconclusive) };
    }

    internal JudgeVerdict RecordObservation(string text, JudgeVerdict verdict, string? deterministicRule = null)
    {
        var user = JudgePromptRenderer.Render(_resolved.Definition, text, string.Empty, null);
        return verdict with { Instrument = new(Digest, JudgePromptRenderer.Provenance(_resolved, user),
            _options.ModelId, [], verdict.Decision != JudgeDecision.Inconclusive)
            { DeterministicRule = deterministicRule } };
    }

    private static bool TryRead(string? text, out JudgeVerdict result)
    {
        result = JudgeVerdict.Inconclusive();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1048576) return false;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JudgeResponseReader.HasDuplicateProperties(root) ||
                root.EnumerateObject().Any(p => p.Name is not ("decision" or "confidence" or "rationale" or "evidence")) ||
                !root.TryGetProperty("decision", out var decisionNode) || decisionNode.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("confidence", out var confidenceNode) || confidenceNode.ValueKind != JsonValueKind.Number ||
                !confidenceNode.TryGetDouble(out var confidence) || !double.IsFinite(confidence) || confidence is < 0 or > 1 ||
                !root.TryGetProperty("rationale", out var rationaleNode) || rationaleNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rationaleNode.GetString()) ||
                !root.TryGetProperty("evidence", out var evidenceNode) || evidenceNode.ValueKind != JsonValueKind.Array || evidenceNode.GetArrayLength() > 256 ||
                evidenceNode.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) return false;
            var decision = decisionNode.GetString() switch
            {
                "ALLOWED" => JudgeDecision.Allowed, "BLOCKED" => JudgeDecision.Blocked,
                "INCONCLUSIVE" => JudgeDecision.Inconclusive, _ => (JudgeDecision?)null
            };
            if (decision is null) return false;
            result = new(decision.Value, confidence, Array.AsReadOnly(evidenceNode.EnumerateArray().Select(e => e.GetString()!).ToArray()), rationaleNode.GetString());
            return true;
        }
        catch (JsonException) { return false; }
    }
}
