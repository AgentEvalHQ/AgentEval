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
    private bool _hasInput;
    private bool _hasOutput;

    public void Add(UsageDetails usage)
    {
        if (usage.InputTokenCount is { } input)
        {
            _hasInput = true;
            _input = TokenUsageMapping.Add(_input, input);
        }
        if (usage.OutputTokenCount is { } output)
        {
            _hasOutput = true;
            _output = TokenUsageMapping.Add(_output, output);
        }
    }

    public TokenUsage? ToTokenUsage() => _hasInput && _hasOutput
        ? TokenUsageMapping.FromUsage(new UsageDetails { InputTokenCount = _input, OutputTokenCount = _output })
        : null;
}
