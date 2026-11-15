// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>Collects additive provider counts without inventing missing or invalid components.</summary>
internal sealed class StreamingUsageAccumulator
{
    private long? _input = 0;
    private long? _output = 0;
    private long? _cachedInput = 0;
    private bool _hasInput;
    private bool _hasOutput;

    public void Add(UsageDetails usage)
    {
        if (usage.InputTokenCount is { } input)
        {
            _hasInput = true;
            _input = TokenUsageMapping.Add(_input, input);
            _cachedInput = TokenUsageMapping.AddCached(_cachedInput, usage);
        }
        if (usage.OutputTokenCount is { } output)
        {
            _hasOutput = true;
            _output = TokenUsageMapping.Add(_output, output);
        }
        // Output-only frames do not imply unknown input-cache usage. A separate cache-only
        // contribution is additive, but cannot repair a previously unreported cache count.
        if (usage.InputTokenCount is null && usage.CachedInputTokenCount.HasValue)
            _cachedInput = TokenUsageMapping.AddCached(_cachedInput, usage);
    }

    public TokenUsage? ToTokenUsage(bool hasMixedModelUsage = false) => _hasInput && _hasOutput
        ? TokenUsageMapping.FromUsage(new UsageDetails
        {
            InputTokenCount = _input, OutputTokenCount = _output, CachedInputTokenCount = _cachedInput
        }, hasMixedModelUsage)
        : null;
}
