// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.LongMemEval;

public partial class LongMemEvalBenchmarkRunner
{
    private const int MaximumModelIdentityLength = 256;

    /// <summary>
    /// Runs the normal memory agent to completion, then runs a retrieval-bypassing oracle
    /// reader over a sanitized projection of the same frozen selected questions.
    /// </summary>
    /// <remarks>
    /// The declared answer-model fields in <paramref name="normalConfig"/> and
    /// <paramref name="oracleConfig"/> must match. AgentEval records those declarations
    /// but cannot prove the configuration of an opaque normal agent.
    /// </remarks>
    public async Task<LongMemEvalPairedResult> RunPairedAsync(
        IEvaluableAgent normalAgent,
        AgentBenchmarkConfig normalConfig,
        IChatClient oracleAnswerClient,
        AgentBenchmarkConfig oracleConfig,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(normalAgent);
        ArgumentNullException.ThrowIfNull(normalConfig);
        ArgumentNullException.ThrowIfNull(oracleAnswerClient);
        ArgumentNullException.ThrowIfNull(oracleConfig);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateMatchingAnswerModels(normalConfig, oracleConfig);

        var entries = LoadEntries(options);
        var selectedQuestionIds = Array.AsReadOnly(
            entries.Select(entry => entry.QuestionId).ToArray());

        // This await is a security boundary: no oracle projection or oracle reader exists
        // until the normal path has fully completed.
        var normal = await RunSelectedAsync(
            normalAgent,
            options,
            entries,
            executionLabel: "normal",
            ct);

        ct.ThrowIfCancellationRequested();
        var oracleEntries = entries
            .Select(LongMemEvalOracleProjector.Project)
            .ToArray();
        var oracleReader = new LongMemEvalOracleReader(oracleAnswerClient);
        var oracle = await RunSelectedAsync(
            oracleReader,
            options,
            oracleEntries,
            executionLabel: "oracle",
            ct);

        double? oracleGap =
            normal.OverallAccuracy.HasValue && oracle.OverallAccuracy.HasValue
                ? oracle.OverallAccuracy.Value - normal.OverallAccuracy.Value
                : null;

        return new LongMemEvalPairedResult
        {
            Normal = normal,
            Oracle = oracle,
            SelectedQuestionIds = selectedQuestionIds,
            NormalAnswerModel = LongMemEvalModelIdentity.From(normalConfig),
            OracleAnswerModel = LongMemEvalModelIdentity.From(
                oracleConfig,
                GetReportedModelId(oracleAnswerClient)),
            JudgeModelId = GetReportedModelId(_chatClient),
            OracleGapPercentagePoints = oracleGap
        };
    }

    private static string? GetReportedModelId(IChatClient client)
    {
        try
        {
            var modelId =
                (client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;
            if (string.IsNullOrWhiteSpace(modelId))
                return null;

            var trimmed = modelId.Trim();
            return trimmed.Length <= MaximumModelIdentityLength && !trimmed.Any(char.IsControl)
                ? trimmed
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateMatchingAnswerModels(
        AgentBenchmarkConfig normalConfig,
        AgentBenchmarkConfig oracleConfig)
    {
        ValidateModelDeclaration(normalConfig, nameof(normalConfig));
        ValidateModelDeclaration(oracleConfig, nameof(oracleConfig));

        if (!string.Equals(normalConfig.ModelId, oracleConfig.ModelId, StringComparison.Ordinal) ||
            !string.Equals(normalConfig.ModelVersion, oracleConfig.ModelVersion, StringComparison.Ordinal) ||
            normalConfig.Temperature != oracleConfig.Temperature ||
            normalConfig.MaxTokens != oracleConfig.MaxTokens)
        {
            throw new ArgumentException(
                "Oracle answer-model declaration must match the normal answer-model " +
                "ModelId, ModelVersion, Temperature, and MaxTokens.",
                nameof(oracleConfig));
        }
    }

    private static void ValidateModelDeclaration(
        AgentBenchmarkConfig config,
        string parameterName)
    {
        if (!IsValidDeclaredIdentity(config.ModelId, MaximumModelIdentityLength))
        {
            throw new ArgumentException(
                "Paired LongMemEval execution requires a trimmed, bounded, " +
                "control-character-free ModelId.",
                parameterName);
        }
        if (config.ModelVersion is not null &&
            !IsValidDeclaredIdentity(config.ModelVersion, MaximumModelIdentityLength))
        {
            throw new ArgumentException(
                "ModelVersion must be trimmed, bounded, and control-character-free when supplied.",
                parameterName);
        }
        if (config.Temperature is { } temperature && !double.IsFinite(temperature))
        {
            throw new ArgumentException(
                "Paired LongMemEval execution requires a finite Temperature.",
                parameterName);
        }
        if (config.MaxTokens is <= 0)
        {
            throw new ArgumentException(
                "Paired LongMemEval execution requires MaxTokens to be positive when supplied.",
                parameterName);
        }
    }

    private static bool IsValidDeclaredIdentity(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}
