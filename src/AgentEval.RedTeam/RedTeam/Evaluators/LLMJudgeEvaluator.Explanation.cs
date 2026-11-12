// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Prompts;

namespace AgentEval.RedTeam.Evaluators;

public partial class LLMJudgeEvaluator
{
    internal const string ExplanationReaderVersion = "redteam.explanation-json.v1";
    internal const string ExplanationEvidenceVersion = "redteam.explanation-facts.v1";

    /// <summary>Exports the narrative contract separately from the verdict judge.</summary>
    public JudgePromptDefinition CaptureExplanationPromptDefinition() => RedTeamJudgePrompts.ForExplanation(_options);

    /// <summary>Explains an existing verdict using an isolated prompt selection, overriding constructor selection.</summary>
    /// <remarks>Unavailable narration returns null text and records attempted calls; caller cancellation propagates.
    /// The explanation never establishes or changes a verdict or fidelity.</remarks>
    public Task<ProbeExplanation> ExplainWithPromptsAsync(AttackProbe probe, string response,
        EvaluationOutcome outcome, EvidenceFidelity fidelity, JudgePromptOptions prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ConfiguredProbeJudge.ValidateSettings(_options);
        var resolved = new JudgePromptResolver().Resolve(CaptureExplanationPromptDefinition(), prompts);
        return ExplainResolvedAsync(resolved, probe, response, outcome, fidelity, "response", null,
            ExplanationConfigurationDigest(resolved, _options, null), cancellationToken, cancellationToken);
    }

    internal static string ExplanationConfigurationDigest(ResolvedJudgePrompt resolved, LLMJudgeOptions settings,
        TimeSpan? timeout) => ProbeJudgeProvenance.Digest(new
        {
            prompt = resolved.Provenance.Digest, settings.Temperature, settings.MaxTokens,
            timeoutTicks = timeout?.Ticks, reader = ExplanationReaderVersion, evidence = ExplanationEvidenceVersion
        });

    internal async Task<ProbeExplanation> ExplainResolvedAsync(ResolvedJudgePrompt resolved, AttackProbe probe,
        string response, EvaluationOutcome outcome, EvidenceFidelity fidelity, string evidenceKind, string? verdictReason,
        string configurationDigest, CancellationToken executionToken, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(response);
        if (!Enum.IsDefined(outcome) || !Enum.IsDefined(fidelity))
            throw new ArgumentException("Expected: defined outcome and evidence fidelity. Actual: unknown enum value. Suggestions: supply the established verdict. Because: narration cannot invent its source facts.");
        var context = JsonSerializer.Serialize(new { id = probe.Id, attack = probe.AttackName, technique = probe.Technique,
            outcome = outcome.ToString(), fidelity = fidelity.ToString(), verdictReason, evidenceKind });
        var reply = await ConfiguredProbeTransport.RunAsync<string>(resolved, probe.Prompt, response, context,
            _options, _chatClient, (messages, send, ct) => GetJudgeResponseAsync(messages, ct, send), TryReadExplanation,
            ExplanationReaderVersion, ExplanationEvidenceVersion, executionToken, callerToken).ConfigureAwait(false);
        return new(reply.Parsed ? reply.Value : null,
            reply.Instrument with { ConfigurationDigest = configurationDigest, Measured = reply.Parsed }, reply.Failure);
    }

    private static bool TryReadExplanation(string? text, JudgePromptDefinition definition, out string value)
    {
        value = string.Empty;
        if (definition.ResponseContract != JudgeResponseContract.RedTeamExplanationV1 ||
            string.IsNullOrWhiteSpace(text) || text.Length > 1048576) return false;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JudgeResponseReader.HasDuplicateProperties(root) ||
                root.EnumerateObject().Any(p => p.Name != "explanation") ||
                !root.TryGetProperty("explanation", out var explanation) || explanation.ValueKind != JsonValueKind.String)
                return false;
            var narrative = explanation.GetString()!;
            if (narrative.Length > 8192 || string.IsNullOrWhiteSpace(narrative)) return false;
            value = narrative.Trim();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
