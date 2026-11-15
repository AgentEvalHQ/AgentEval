// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam.Evaluators;

public sealed partial class LikertJudgeEvaluator
{
    /// <summary>Exports this judge's text and normalized rating policy without invoking the provider.</summary>
    public JudgePromptDefinition CapturePromptDefinition() => RedTeamJudgePrompts.ForLikert(_options);

    /// <summary>Judges one probe with an isolated prompt selection, overriding constructor selection.</summary>
    public Task<EvaluationResult> EvaluateWithPromptsAsync(AttackProbe probe, string response, JudgePromptOptions prompts,
        CancellationToken cancellationToken = default) => ConfiguredProbeJudge.EvaluateAsync(probe, response,
            CapturePromptDefinition(), prompts, _options, _chatClient,
            (_, send, _) => send(new ChatOptions { Temperature = _options.Temperature, MaxOutputTokens = _options.MaxTokens }), cancellationToken);
}
