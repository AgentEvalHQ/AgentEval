// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Prompts;

namespace AgentEval.RedTeam.Evaluators;

/// <summary>Preflight and best-effort narration, independent of grading and its identity.</summary>
internal sealed class ConfiguredProbeNarrator(LLMJudgeEvaluator judge, ResolvedJudgePrompt prompt, TimeSpan? timeout,
    string digest)
{
    internal string Digest => digest;

    internal static ConfiguredProbeNarrator? Create(ScanOptions options)
    {
        if (!options.ExplainFindings || (options.JudgePrompts is null && options.ExplanationJudgeOptions is null)) return null;
        if (options.JudgeClient is null)
            throw new NotSupportedException("Expected: a judge client for configured explanations. Actual: none. Suggestions: set JudgeClient. Because: a selected narrative instrument cannot be silently skipped.");
        if (options.JudgeTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Expected: positive executable judge timeout. Actual: invalid duration. Suggestions: use a positive duration below the timer limit or null. Because: narration requires an executable time budget.");
        var settings = options.ExplanationJudgeOptions ?? new LLMJudgeOptions();
        ConfiguredProbeJudge.ValidateSettings(settings);
        var judge = new LLMJudgeEvaluator(options.JudgeClient, settings);
        var resolved = new JudgePromptResolver().Resolve(judge.CaptureExplanationPromptDefinition(),
            settings.JudgePrompts ?? options.JudgePrompts ?? new());
        return new(judge, resolved, options.JudgeTimeout,
            LLMJudgeEvaluator.ExplanationConfigurationDigest(resolved, settings, options.JudgeTimeout));
    }

    internal async Task<ProbeResult> AttachAsync(AttackProbe probe, ProbeResult result, bool includeEvidence,
        CancellationToken executionToken, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (!includeEvidence || result.HasError || result.Outcome == EvaluationOutcome.Resisted) return result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
        if (timeout is { } duration) deadline.CancelAfter(duration);
        var watch = Stopwatch.StartNew();
        var explanation = await judge.ExplainResolvedAsync(prompt, probe, result.Response, result.Outcome, result.Fidelity,
            result.TurnsUsed is not null || result.NodesExplored is not null ? "transcript" : "response", result.Reason,
            digest, deadline.Token, callerToken).ConfigureAwait(false);
        return result with { Rationale = explanation.Text, ExplanationInstrument = explanation.Instrument,
            Duration = result.Duration + watch.Elapsed };
    }
}
