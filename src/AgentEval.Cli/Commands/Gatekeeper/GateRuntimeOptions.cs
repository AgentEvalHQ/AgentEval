// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using AgentEval.Guardrails.Judges;
using AgentEval.Prompts;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>Identical execution settings for configured CLI calibration and inspection.</summary>
internal sealed class GateRuntimeOptions(Command command)
{
    private readonly Option<double?> _timeout = Add<double?>(command, "--judge-timeout-seconds", "Total judge deadline, including corrective retry (default 5 seconds).");
    private readonly Option<int?> _tokens = Add<int?>(command, "--judge-max-output-tokens", "Output token limit per attempt (default 1024).");
    private readonly Option<float?> _temperature = Add<float?>(command, "--judge-temperature", "Explicit judge temperature; omitted uses provider default.");
    private readonly Option<int?> _input = Add<int?>(command, "--judge-max-input-chars", "Head/tail input limit (default 16000; 0 means unbounded).");

    internal GateRuntimeSettings Read(ParseResult parse) => new(parse.GetValue(_timeout), parse.GetValue(_tokens),
        parse.GetValue(_temperature), parse.GetValue(_input));

    private static Option<T> Add<T>(Command command, string name, string description)
    {
        var option = new Option<T>(name) { Description = description };
        command.Options.Add(option);
        return option;
    }
}

internal sealed record GateRuntimeSettings(double? TimeoutSeconds = null, int? MaxOutputTokens = null,
    float? Temperature = null, int? MaxInputChars = null)
{
    internal JudgeGateOptions? Bind(JudgePromptOptions? prompts, string? model)
    {
        if (prompts is null)
        {
            if (TimeoutSeconds is not null || MaxOutputTokens is not null || Temperature is not null || MaxInputChars is not null)
                throw new ArgumentException("Expected: --judge-prompt-pack with execution overrides. Actual: legacy judge selection. Suggestions: export and select the gate pack. Because: legacy certificates do not identify custom execution settings.");
            return null;
        }
        if (TimeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue))
            throw new ArgumentException("Expected: a positive finite judge deadline within 24.8 days. Actual: invalid timeout. Suggestions: set --judge-timeout-seconds to 45, for example. Because: the judge needs a bounded executable deadline.");
        var defaults = new JudgeGateOptions();
        return new()
        {
            JudgePrompts = prompts, ModelId = model,
            Timeout = TimeoutSeconds is { } value ? TimeSpan.FromSeconds(value) : defaults.Timeout,
            MaxOutputTokens = MaxOutputTokens ?? defaults.MaxOutputTokens,
            Temperature = Temperature, MaxInputChars = MaxInputChars ?? defaults.MaxInputChars
        };
    }
}
