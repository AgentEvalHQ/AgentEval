// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;

namespace AgentEval.RedTeam.Evaluators;

public partial class LLMJudgeEvaluator
{
    /// <summary>Exports this judge's text and verdict-reading policy without invoking the provider.</summary>
    public JudgePromptDefinition CapturePromptDefinition() => RedTeamJudgePrompts.ForVerdict(_options);

    /// <summary>Judges one probe with an isolated prompt selection, overriding constructor selection.</summary>
    public Task<EvaluationResult> EvaluateWithPromptsAsync(AttackProbe probe, string response, JudgePromptOptions prompts,
        CancellationToken cancellationToken = default) => ConfiguredProbeJudge.EvaluateAsync(probe, response,
            CapturePromptDefinition(), prompts, _options, _chatClient,
            (messages, send, ct) => GetJudgeResponseAsync(messages, ct, send), cancellationToken);

    internal Task<EvaluationResult> EvaluateWithPromptsAsync(AttackProbe probe, string response, JudgePromptOptions prompts,
        CancellationToken executionToken, CancellationToken callerToken) => ConfiguredProbeJudge.EvaluateAsync(probe, response,
            CapturePromptDefinition(), prompts, _options, _chatClient,
            (messages, send, ct) => GetJudgeResponseAsync(messages, ct, send), executionToken, callerToken);
}
