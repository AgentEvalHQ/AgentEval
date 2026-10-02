// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.Providers;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The judge a <c>bench … calibrate</c> run graded with: the provider that served it and the model or deployment
/// it was built for. Every calibration report carries it in its header, and the agentic per-case records carry it
/// on every line.
/// </summary>
/// <remarks>
/// <para>
/// A calibration figure describes one judge model, and readers are told to calibrate for their own. Before this,
/// the reports said only when they were generated and which thresholds applied, so two reports produced by
/// different deployments could not be told apart, and a judge swapped by an environment variable between two runs
/// did not show up in a diff of the reports.
/// </para>
/// <para>
/// Never a key and never an endpoint. An endpoint URL can carry a credential in its user-info, path, query or
/// fragment, and these reports are written to be kept. Provider and model are enough to tell two runs apart.
/// </para>
/// </remarks>
/// <param name="Provider">Who served the judge, and how it was chosen.</param>
/// <param name="Model">The model or deployment the judge was built for.</param>
internal sealed record CalibrationJudgeIdentity(string Provider, string Model)
{
    /// <summary>The variables <see cref="JudgeFactory"/> reads first, for a judge on its own Azure OpenAI endpoint.</summary>
    private static readonly string[] s_judgeSpecificVariables =
        ["AZURE_OPENAI_JUDGE_ENDPOINT", "AZURE_OPENAI_JUDGE_API_KEY", "AZURE_OPENAI_JUDGE_DEPLOYMENT"];

    /// <summary>
    /// Identifies the judge from the process environment. The overload that takes a variable source documents the
    /// rules.
    /// </summary>
    internal static CalibrationJudgeIdentity Of(
        IEvaluator? evaluatorOverride,
        CalibrationJudgeIdentity? overrideIdentity,
        IEvaluator judge,
        string judgeModel) =>
        Of(evaluatorOverride, overrideIdentity, judge, judgeModel, Environment.GetEnvironmentVariable);

    /// <summary>Identifies the judge that <see cref="JudgeFactory"/> returned.</summary>
    /// <param name="evaluatorOverride">The evaluator the caller supplied, or <see langword="null"/> when the environment chose the judge.</param>
    /// <param name="overrideIdentity">
    /// What the caller says <paramref name="evaluatorOverride"/> is. Ignored when there is no override: the
    /// environment chose that judge, and a caller-supplied name for it could be wrong.
    /// </param>
    /// <param name="judge">The judge <see cref="JudgeFactory"/> returned.</param>
    /// <param name="judgeModel">The model name <see cref="JudgeFactory"/> returned with it.</param>
    /// <param name="getEnvironmentVariable">The variable source; tests pass a dictionary.</param>
    /// <remarks>
    /// <see cref="JudgeFactory"/> returns the model but not the provider, so the provider is read back from the
    /// variables it resolved, in the order it resolves them: the <c>AZURE_OPENAI_JUDGE_*</c> trio first, then the
    /// provider <c>AI_INFERENCE_PROVIDER</c> selects or auto-detects. A provider is named only when those variables
    /// still resolve to the model the judge was built with. Otherwise the provider is reported as unknown rather
    /// than guessed.
    /// </remarks>
    internal static CalibrationJudgeIdentity Of(
        IEvaluator? evaluatorOverride,
        CalibrationJudgeIdentity? overrideIdentity,
        IEvaluator judge,
        string judgeModel,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(judgeModel);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (evaluatorOverride is not null)
        {
            return overrideIdentity is not null
                ? new(OneLine(overrideIdentity.Provider), OneLine(overrideIdentity.Model))
                : new($"unknown: an evaluator supplied by the caller ({evaluatorOverride.GetType().Name}), not resolved from the environment",
                      "unknown: the caller did not name it");
        }

        if (judge is JudgeFactory.StubEvaluator)
        {
            return new(
                "stub (AGENTEVAL_ALLOW_STUB_JUDGE=1): a fixed placeholder verdict for every case, so these figures measure no model",
                "none");
        }

        var model = OneLine(judgeModel);
        var judgeSpecificSet = s_judgeSpecificVariables.Count(n => !string.IsNullOrWhiteSpace(getEnvironmentVariable(n)));

        // The judge-specific Azure trio wins outright in JudgeFactory when all three are set, and a partial trio is
        // refused there, so a partial trio here means the environment changed after the judge was built.
        if (judgeSpecificSet == s_judgeSpecificVariables.Length)
        {
            return string.Equals(getEnvironmentVariable("AZURE_OPENAI_JUDGE_DEPLOYMENT"), judgeModel, StringComparison.Ordinal)
                ? new("Azure OpenAI (judge-specific endpoint, AZURE_OPENAI_JUDGE_*)", model)
                : Unknown(model);
        }
        if (judgeSpecificSet > 0)
            return Unknown(model);

        // Otherwise the provider AI_INFERENCE_PROVIDER selects, or the first one with credentials.
        var settings = InferenceProviderEnvironment.Resolve(getEnvironmentVariable);
        if (!settings.IsConfigured || !string.Equals(settings.Model, judgeModel, StringComparison.Ordinal))
            return Unknown(model);

        var how = settings.Selection == InferenceProviderSelection.Explicit
            ? $"{InferenceProviderEnvironment.SelectorVariable}={settings.ProviderTag}"
            : $"{settings.ProviderTag}, auto-detected";
        return new($"{settings.DisplayName} ({how})", model);
    }

    /// <summary>Writes the two header lines of a calibration report, each followed by a blank line.</summary>
    internal void AppendMarkdownHeader(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.AppendLine($"Judge provider: {Provider}");
        sb.AppendLine();
        sb.AppendLine($"Judge model: {Model}");
        sb.AppendLine();
    }

    private static CalibrationJudgeIdentity Unknown(string model) =>
        new("unknown: the environment no longer resolves to the provider this judge was built from", model);

    /// <summary>Keeps a header value on one line: a control character in a configured name becomes a space.</summary>
    private static string OneLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }
}
